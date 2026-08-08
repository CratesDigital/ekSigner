using System.Net;

namespace EtaSignAgent;

/// <summary>
/// The agent's front page, served on 127.0.0.1.
///
/// This exists because the installed agent has no console. The pairing code
/// used to be printed to a terminal the operator had opened themselves; once
/// the agent starts at login from an installer, there is no terminal and no
/// window, so the code has to live somewhere the operator can actually reach.
/// The browser is that place — it needs no GUI toolkit and no tray icon.
///
/// Self-contained like the unlock page: no external asset, nothing from ekPOS.
/// </summary>
internal static class StatusPage
{
    public static string Html(AgentConfig config, TokenService tokens)
    {
        var modules = tokens.LoadedModules.Count == 0
            ? "<div class=\"bad\">No token driver found on this computer. Install the driver that came "
              + "with your ePass2003 or PROXKey token, then restart the agent.</div>"
            : "<ul class=\"list\">" + string.Join("", tokens.LoadedModules.Select(
                  m => "<li>" + Esc(System.IO.Path.GetFileName(m)) + "</li>")) + "</ul>";

        var paired = config.PairedOrigins.Count == 0
            ? "<p class=\"muted\">No site paired yet — enter the code above in ekPOS.</p>"
            : "<ul class=\"list\">" + string.Join("", config.PairedOrigins.Select(
                  o => "<li>" + Esc(o) + "</li>")) + "</ul>";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ekPOS Signing Agent</title>
              <style>
                :root { color-scheme: light dark; }
                body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                       margin: 0; padding: 32px 16px; background: #f8fafc; color: #0f172a; }
                .card { background: #fff; border: 1px solid #e2e8f0; border-radius: 14px;
                        padding: 28px; width: 100%; max-width: 460px; margin: 0 auto;
                        box-shadow: 0 8px 24px rgba(15,23,42,.06); }
                h1 { font-size: 18px; margin: 0 0 2px; }
                h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .06em;
                     color: #64748b; margin: 26px 0 8px; }
                p  { font-size: 13px; color: #475569; margin: 0 0 10px; line-height: 1.55; }
                .muted { color: #94a3b8; }
                .addr { font-family: ui-monospace, Consolas, monospace; font-size: 12px; color: #94a3b8; }
                .code { font-family: ui-monospace, Consolas, monospace; font-size: 34px;
                        font-weight: 700; letter-spacing: .22em; color: #0f172a;
                        background: #f1f5f9; border: 1px solid #e2e8f0; border-radius: 10px;
                        padding: 14px; text-align: center; }
                .list { margin: 0; padding-inline-start: 20px; font-size: 13px; color: #475569; }
                .list li { margin-bottom: 3px; font-family: ui-monospace, Consolas, monospace; }
                .row { display: flex; align-items: center; gap: 10px; margin-top: 10px; flex-wrap: wrap; }
                .badge { font-size: 12px; font-weight: 600; padding: 5px 11px; border-radius: 999px; }
                .on  { background: #f0fdf4; color: #166534; border: 1px solid #bbf7d0; }
                .off { background: #fffbeb; color: #92400e; border: 1px solid #fde68a; }
                .bad { font-size: 13px; padding: 11px 13px; border-radius: 8px;
                       background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; }
                a.btn, button.btn { font: inherit; font-size: 13px; font-weight: 600; padding: 8px 14px;
                       border-radius: 8px; border: 1px solid #cbd5e1; background: #fff; color: #0f172a;
                       cursor: pointer; text-decoration: none; display: inline-block; }
                a.btn.primary { background: #2563eb; border-color: #2563eb; color: #fff; }
                @media (prefers-color-scheme: dark) {
                  body { background: #0f172a; color: #e2e8f0; }
                  .card { background: #1e293b; border-color: #334155; }
                  .code { background: #0f172a; border-color: #334155; color: #e2e8f0; }
                  p, .list { color: #94a3b8; }
                  a.btn, button.btn { background: #0f172a; border-color: #475569; color: #e2e8f0; }
                  a.btn.primary { background: #2563eb; border-color: #2563eb; color: #fff; }
                }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>ekPOS Signing Agent</h1>
                <p class="addr">Running on http://127.0.0.1:{{config.Port}}</p>

                <h2>Pairing code</h2>
                <div class="code">{{config.PairingCode}}</div>
                <p style="margin-top:10px;">
                  In ekPOS, open <strong>Integrations &rarr; ETA &rarr; Connection</strong> and enter this
                  code. Only a site you pair can ask this agent to sign.
                </p>

                <h2>Paired sites</h2>
                {{paired}}

                <h2>Signing token</h2>
                {{modules}}
                <div class="row">
                  <span id="lockState" class="badge off">Checking…</span>
                  <a class="btn primary" href="/unlock">Unlock token</a>
                  <button class="btn" id="lockNow" style="display:none;">Lock now</button>
                </div>
                <p style="margin-top:10px;">
                  The PIN is entered here, on this computer, and never reaches ekPOS.
                  It stays unlocked for {{config.UnlockMinutes}} minutes.
                </p>

                <h2>Leave this running</h2>
                <p>
                  The agent starts automatically when you sign in to Windows. Close this tab —
                  it keeps running. To come back, open
                  <a href="http://127.0.0.1:{{config.Port}}/">127.0.0.1:{{config.Port}}</a>.
                </p>
              </div>

              <script>
                const state = document.getElementById('lockState');
                const lockNow = document.getElementById('lockNow');

                async function refresh() {
                  try {
                    const d = await (await fetch('/v1/ping')).json();
                    if (d.unlocked) {
                      const until = d.unlocked_until ? new Date(d.unlocked_until) : null;
                      state.textContent = until
                        ? 'Unlocked until ' + until.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
                        : 'Unlocked';
                      state.className = 'badge on';
                      lockNow.style.display = '';
                    } else {
                      state.textContent = 'Locked';
                      state.className = 'badge off';
                      lockNow.style.display = 'none';
                    }
                  } catch (e) {
                    state.textContent = 'Agent stopped';
                    state.className = 'badge off';
                  }
                }

                lockNow.addEventListener('click', async () => {
                  await fetch('/v1/lock', { method: 'POST' });
                  refresh();
                });

                refresh();
                setInterval(refresh, 5000);
              </script>
            </body>
            </html>
            """;
    }

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
