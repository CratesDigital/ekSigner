namespace EtaSignAgent;

/// <summary>
/// The PIN entry page, served by the agent on 127.0.0.1.
///
/// Self-contained on purpose: the PIN is typed into a page the agent itself
/// serves, posted straight back to the agent, and never leaves the machine. No
/// No caller script runs here and no external asset is loaded, so nothing can read
/// the field but the agent.
/// </summary>
internal static class UnlockPage
{
    public static string Html(AgentConfig config) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>ekSign</title>
          <style>
            :root { color-scheme: light dark; }
            body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                   display: flex; align-items: center; justify-content: center;
                   min-height: 100vh; margin: 0; background: #f8fafc; color: #0f172a; }
            .card { background: #fff; border: 1px solid #e2e8f0; border-radius: 14px;
                    padding: 28px; width: 100%; max-width: 380px;
                    box-shadow: 0 8px 24px rgba(15,23,42,.06); }
            h1 { font-size: 17px; margin: 0 0 4px; }
            p  { font-size: 13px; color: #64748b; margin: 0 0 18px; line-height: 1.5; }
            label { display: block; font-size: 13px; font-weight: 600; margin-bottom: 6px; }
            input { width: 100%; box-sizing: border-box; padding: 10px 12px; font-size: 15px;
                    border: 1px solid #cbd5e1; border-radius: 8px; letter-spacing: .18em; }
            input:focus { outline: none; border-color: #2563eb; }
            button { width: 100%; margin-top: 14px; padding: 11px; font-size: 14px; font-weight: 600;
                     color: #fff; background: #2563eb; border: 0; border-radius: 8px; cursor: pointer; }
            button:disabled { opacity: .6; cursor: progress; }
            .msg { margin-top: 14px; font-size: 13px; padding: 10px 12px; border-radius: 8px; display: none; }
            .err { background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; }
            .ok  { background: #f0fdf4; color: #166534; border: 1px solid #bbf7d0; }
            .warn { margin-top: 16px; font-size: 12px; color: #92400e;
                    background: #fffbeb; border: 1px solid #fde68a; border-radius: 8px; padding: 10px 12px; }
            @media (prefers-color-scheme: dark) {
              body { background: #0f172a; color: #e2e8f0; }
              .card { background: #1e293b; border-color: #334155; }
              input { background: #0f172a; border-color: #475569; color: #e2e8f0; }
              p { color: #94a3b8; }
            }
          </style>
        </head>
        <body>
          <div class="card">
            <h1>Unlock signing token</h1>
            <p>Your PIN is checked by the agent on this computer. It is never sent anywhere.</p>

            <label for="pin">Token PIN</label>
            <input id="pin" type="password" inputmode="numeric" autocomplete="off" autofocus>
            <button id="go">Unlock for {{config.UnlockMinutes}} minutes</button>

            <div id="msg" class="msg"></div>

            <div class="warn">
              One attempt is sent per click. These tokens give no warning before
              locking permanently, so be sure of the PIN before unlocking.
            </div>
          </div>

          <script>
            const pin = document.getElementById('pin');
            const go  = document.getElementById('go');
            const msg = document.getElementById('msg');

            async function unlock() {
              if (!pin.value) return;
              go.disabled = true;
              msg.style.display = 'none';
              try {
                const r = await fetch('/v1/unlock', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ pin: pin.value }),
                });
                const d = await r.json();
                msg.className = 'msg ' + (d.ok ? 'ok' : 'err');
                msg.textContent = d.ok
                  ? 'Unlocked. You can close this tab and go back to signing.'
                  : (d.message || 'The PIN was not accepted.');
                msg.style.display = 'block';
                if (d.ok) pin.value = '';
              } catch (e) {
                msg.className = 'msg err';
                msg.textContent = 'The agent did not respond.';
                msg.style.display = 'block';
              }
              go.disabled = false;
            }

            go.addEventListener('click', unlock);
            pin.addEventListener('keydown', e => { if (e.key === 'Enter') unlock(); });
          </script>
        </body>
        </html>
        """;
}
