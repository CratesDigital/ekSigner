using System.Diagnostics;
using EtaSignAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var config = AgentConfig.Load();

// Only one agent per user session. Two would contend for the token — vendor
// PKCS#11 modules take exclusive access — and the second would fail to bind
// anyway.
//
// Starting an agent that is already started is a request to SEE it, not an
// error: the operator clicked the shortcut because they wanted the pairing code
// or the unlock page. So show them that, and leave quietly. The startup error
// dialog stays for the case it was written for — something else on the port.
using var instanceLock = new Mutex(initiallyOwned: true, @"Local\eksign", out var isFirstInstance);
if (!isFirstInstance)
{
    try
    {
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{config.Port}/") { UseShellExecute = true });
    }
    catch
    {
        // Nothing to report to: no console, and a dialog here would be the very
        // thing this replaced.
    }
    return 0;
}

Autostart.Apply(config);

var tokens = new TokenService(config.ModulePaths());

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
// Loopback only. Binding 0.0.0.0 would expose a signing oracle to the LAN.
builder.WebHost.UseUrls($"http://127.0.0.1:{config.Port}");
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(tokens);

var app = builder.Build();

// ── CORS + Private Network Access ────────────────────────────────
//
// Loopback is a "potentially trustworthy origin", so an HTTPS page may
// call http://127.0.0.1 without mixed-content blocking — which is why the agent
// needs no certificate and no browser trust prompt.
//
// But Chrome's Private Network Access sends
// `Access-Control-Request-Private-Network: true` on the preflight for a
// public → loopback request, and REFUSES the call unless the response says
// `Access-Control-Allow-Private-Network: true`. Omit that one header and the
// failure is indistinguishable from the agent not running at all.
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();

    // Echoed for EVERY origin, paired or not. CORS is not the security boundary
    // here — `Allowed()` is, and it still refuses an unpaired caller. Gating the
    // header on pairing made pairing impossible: /v1/pair's own preflight comes
    // from an origin that is by definition not yet paired, so the browser
    // blocked it before the handler could record anything, and blocked /v1/ping
    // for the same reason — which made a running agent look like an absent one.
    if (!string.IsNullOrEmpty(origin))
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Vary"] = "Origin";
    }
    context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

// Anything that touches the token requires a paired origin. Requests with no
// Origin at all (curl, the agent's own unlock page) are same-machine and are
// allowed through — the browser is the untrusted caller here, not the shell.
bool Allowed(HttpContext ctx)
{
    var origin = ctx.Request.Headers.Origin.ToString();
    return string.IsNullOrEmpty(origin) || config.IsPaired(origin);
}

IResult Forbidden() => Results.Json(
    new { error = "not_paired", message = "This site is not paired with the signing agent." },
    statusCode: StatusCodes.Status403Forbidden);

// ── Discovery ────────────────────────────────────────────────────

app.MapGet("/v1/ping", (HttpContext ctx) => Results.Json(new
{
    ok       = true,
    agent    = "eksign",
    version  = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0",
    modules  = tokens.LoadedModules,
    unlocked = tokens.IsUnlocked,
    unlocked_until = tokens.UnlockedUntil,
    // Whether THIS caller is paired. Without it a site cannot tell an already
    // paired site from a fresh one, so it had to show the pairing box forever
    // and leave the operator guessing whether they had done it.
    paired   = config.IsPaired(ctx.Request.Headers.Origin.ToString()),
}));

app.MapGet("/v1/certificates", (HttpContext ctx) =>
{
    if (!Allowed(ctx)) return Forbidden();

    try
    {
        var certificates = tokens.ListCertificates().Select(c => new
        {
            thumbprint = c.Thumbprint,
            token      = c.TokenLabel,
            subject    = c.Subject,
            issuer     = c.Issuer,
            not_after  = c.NotAfter.ToString("yyyy-MM-dd"),
            expired    = c.Expired,
        });
        return Results.Json(new { ok = true, certificates });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: 500);
    }
});

// ── Signing ──────────────────────────────────────────────────────

app.MapPost("/v1/sign", async (HttpContext ctx) =>
{
    if (!Allowed(ctx)) return Forbidden();

    var request = await ctx.Request.ReadFromJsonAsync<SignRequest>();
    if (request is null || string.IsNullOrWhiteSpace(request.Canonical) || string.IsNullOrWhiteSpace(request.Thumbprint))
    {
        return Results.Json(new { ok = false, message = "thumbprint and canonical are required." }, statusCode: 400);
    }

    try
    {
        // The agent hashes the canonical string itself. A caller never sends a
        // pre-computed digest, which is what removes any question of the data
        // being hashed twice.
        var signature = tokens.Sign(request.Thumbprint, request.Canonical);
        return Results.Json(new { ok = true, signature });
    }
    catch (TokenLockedException)
    {
        return Results.Json(new
        {
            ok = false,
            error = "locked",
            unlock_url = $"http://127.0.0.1:{config.Port}/unlock",
            message = "The signing token is locked. Unlock it and try again.",
        }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: 500);
    }
});

// ── PIN entry, served BY the agent ───────────────────────────────
//
// The PIN is typed into a page the agent itself serves on 127.0.0.1, so it
// never reaches the calling page or its servers. That is also why no
// GUI toolkit is needed, and why this works identically on Windows and macOS.

app.MapGet("/unlock", () => Results.Content(UnlockPage.Html(config), "text/html; charset=utf-8"));

// The agent's own front page: pairing code, token state, unlock link. An
// installed agent has no console to print the pairing code to, so this page is
// where the operator reads it.
app.MapGet("/", () => Results.Content(StatusPage.Html(config, tokens), "text/html; charset=utf-8"));

app.MapPost("/v1/unlock", async (HttpContext ctx) =>
{
    var request = await ctx.Request.ReadFromJsonAsync<UnlockRequest>();
    if (request is null || string.IsNullOrWhiteSpace(request.Pin))
    {
        return Results.Json(new { ok = false, message = "Enter the token PIN." }, statusCode: 400);
    }

    try
    {
        // One attempt, with the PIN as typed. Never retried: neither token
        // reports how many attempts remain, so a retry loop can lock a
        // taxpayer's e-seal permanently.
        tokens.Unlock(request.Pin, TimeSpan.FromMinutes(config.UnlockMinutes));
        return Results.Json(new { ok = true, until = tokens.UnlockedUntil });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: 400);
    }
});

app.MapPost("/v1/lock", () => { tokens.Lock(); return Results.Json(new { ok = true }); });

// ── Pairing ──────────────────────────────────────────────────────

app.MapPost("/v1/pair", async (HttpContext ctx) =>
{
    var request = await ctx.Request.ReadFromJsonAsync<PairRequest>();
    var origin = ctx.Request.Headers.Origin.ToString();

    if (string.IsNullOrEmpty(origin))
    {
        return Results.Json(new { ok = false, message = "Pairing must be started from the site you are pairing with." }, statusCode: 400);
    }
    if (request is null || request.Code != config.PairingCode)
    {
        return Results.Json(new { ok = false, message = "That pairing code is not correct." }, statusCode: 403);
    }

    config.Pair(origin);
    Console.WriteLine($"[pair] {origin} is now allowed to request signatures.");

    return Results.Json(new { ok = true, origin });
});

Console.WriteLine($"""
    ekSign
      listening : http://127.0.0.1:{config.Port}
      modules   : {(tokens.LoadedModules.Count == 0 ? "NONE FOUND — is a token driver installed?" : string.Join(", ", tokens.LoadedModules))}
      paired    : {(config.PairedOrigins.Count == 0 ? "nothing yet" : string.Join(", ", config.PairedOrigins))}

      Pairing code: {config.PairingCode}
      Enter it on the site you are pairing with, on its ETA settings page.

      Unlock the token at http://127.0.0.1:{config.Port}/unlock
    """);

// Show the front page once, on the run that follows installation — that is the
// run where the operator still has to read a pairing code and enter a PIN.
// After that the agent starts at every login and must stay quiet; an app that
// opens a browser tab each morning gets uninstalled.
if (config.PairedOrigins.Count == 0)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            // UseShellExecute is what hands the URL to the default browser;
            // without it .NET tries to execute the URL as a program.
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{config.Port}/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[start] could not open a browser: {ex.Message}");
        }
    });
}

// Kestrel runs in the background and the tray owns the foreground: NotifyIcon
// needs a message loop, and the loop has to be the thing that blocks, otherwise
// the menu never responds.
try
{
    await app.StartAsync();
}
catch (Exception ex)
{
    // Almost always the port. A second copy of the agent can no longer get here
    // — the mutex above sends it to the status page instead — so this really
    // does mean another program has 8420.
    StartupError.Report(
        $"The signing agent could not start on port {config.Port}.\n\n"
        + $"{ex.Message}\n\n"
        + "Another program may be using that port. Change \"Port\" in\n"
        + "%APPDATA%\\ekSign\\agent-config.json and start the agent again.");
    return 1;
}

// Fire-and-forget rather than blocking the UI thread on StopAsync; the wait
// happens below, off the message loop.
using var tray = new TrayIcon(config, tokens, () => _ = app.StopAsync());
tray.Run();

await app.WaitForShutdownAsync();
return 0;

internal sealed record SignRequest(string Thumbprint, string Canonical, string? Summary);
internal sealed record UnlockRequest(string Pin);
internal sealed record PairRequest(string Code);
