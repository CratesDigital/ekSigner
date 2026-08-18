# ekSign

Replaces the PowerShell spike. Runs on the machine holding the e-seal token,
reads the vendor PKCS#11 module directly, and signs ETA documents on request
from a paired ekPOS site.

The seal produced here is the same CAdES-BES the spike produced — the CMS
builder is carried over unchanged, and ETA has already accepted a document
signed with it (INV00021, portal shows *Signed By: ايكتر للبرمجه والتوريدات*).

## Why it is not the ITIDA client

ITIDA's Web-Sign Client is a portal companion, not an integration API: its own
manual scopes it to signing "through portal", it is launched per-signature via
the `signsrv:` protocol handler rather than listening on a port, it prompts for
the PIN on every signature, and it is Windows-only. See
[the design doc](../../docs/eta-signing-agent-design.md).

## Set-up, once per workstation

1. **Install.** Download `ekSign-Setup.exe` — the Connection tab
   offers it — and run it. No administrator rights and nothing to configure; it
   installs under the user's own profile and starts at every login from then on.
2. **Pair it.** The agent's page opens by itself after installing, showing a
   six-digit code. In ekPOS → Integrations → ETA → Connection, enter that code.
   The agent records that origin and refuses every other one. To see the code
   again later, open <http://127.0.0.1:8420/>.
3. **Unlock the token** at <http://127.0.0.1:8420/unlock> and enter the PIN.
   It stays unlocked for 60 minutes by default.
4. **Sign** from ekPOS → Submissions.

## While it is running

The agent sits in the notification area. Its icon is the only visible sign a
windowless program is running, and its tooltip shows whether the token is
unlocked. Right-click for:

| | |
|---|---|
| **Open agent page** | pairing code, paired sites, token state (also double-click) |
| **Unlock token** | the PIN page |
| **Run when Windows starts** | on by default; the operator can turn it off |
| **Quit** | stops the agent |

Autostart is the `HKCU\...\CurrentVersion\Run` key, written by the agent rather
than by the installer, because the operator has to be able to change it. **Do
not add a Startup-folder shortcut back.** Having both is what made the agent
always already running, so that launching it by hand raised an error dialog.

Starting a second copy is not an error either — the first instance holds a named
mutex, and the second opens the status page and exits.

## Run from source

```powershell
dotnet run
```

Same thing without the installer: listens on `http://127.0.0.1:8420`, puts an
icon in the tray, and opens its page on first run. The build is windowless, so
`Console.WriteLine` output goes nowhere — read the state off the tray tooltip or
the page.

## Releasing

```powershell
git tag sign-agent-v1.0.0
git push origin sign-agent-v1.0.0
```

`.github/workflows/sign-agent.yml` publishes self-contained for `win-x64`,
compiles `installer/eksign.iss`, and attaches the installer to a
GitHub release. It needs a Windows runner — both the publish and Inno Setup do.

### Getting it to the shops

The repo is private, so a GitHub release asset answers a tenant's browser with
a 404. Copy the installer onto the ekPOS host instead:

```bash
scp ekSign-Setup.exe deploy@HOST:/var/www/ekpos/public/downloads/
```

That path is where `config('eta.agent.download_path')` looks, and the download
button in **ETA → Setup → Document signing** appears as soon as the file is
really there — no config change, no restart. Remove the file and the button
goes away rather than leaving a link to a 404. `ETA_AGENT_DOWNLOAD_URL`
overrides the location entirely if the installer is served from somewhere else.

Still open: **the installer is unsigned**, so Windows SmartScreen warns on
first download. The people installing this are shop staff, who are right to be
suspicious of that warning; an OV/EV certificate and a signing step in the
workflow would remove it.

## Where the config lives

`%APPDATA%\ekPOS\agent-config.json` — the pairing, the pairing code, the port,
and any extra PKCS#11 module paths. Deliberately *not* beside the executable:
the installer overwrites its own directory on upgrade, which would cost the
operator their pairing every time the agent updated. A config left beside the
exe by a pre-installer build is moved across on first run.

## Three decisions worth knowing

**Plain HTTP on loopback, not HTTPS.** Loopback is a *potentially trustworthy
origin*, so an HTTPS ekPOS page can call it without mixed-content blocking. That
removes the self-signed-certificate dance that makes the ITIDA client painful to
set up — there is no certificate to install or accept.

The catch is Chrome's Private Network Access: a public → loopback request gets a
preflight carrying `Access-Control-Request-Private-Network: true`, and the call
fails unless the response answers `Access-Control-Allow-Private-Network: true`.
That header is in `Program.cs`. **Remove it and the failure looks exactly like
the agent not running.**

The same is true of `Access-Control-Allow-Origin`, which is echoed for *every*
origin rather than only paired ones. Pairing cannot bootstrap otherwise: the
preflight for `/v1/pair` arrives from an origin that is not yet paired, so
withholding the header there blocks the very call that would pair it. Access is
enforced by the pairing check in each handler, not by the CORS headers.

**The PIN is typed into a page the agent serves.** Not into ekPOS. It never
enters ekPOS's browser context and never reaches its servers. That is also why
there is no GUI toolkit here and why the same build works on Windows and macOS.

**ekPOS sends the canonical string, never a hash.** The agent digests it itself,
so there is no question of the data being hashed twice — the ambiguity that made
the ITIDA client impossible to verify.

## Endpoints

| Method | Path | Notes |
|---|---|---|
| `GET` | `/` | status page: pairing code, paired sites, token state |
| `GET` | `/v1/ping` | version, loaded modules, unlock state. No pairing needed |
| `GET` | `/v1/certificates` | paired origins only |
| `POST` | `/v1/sign` | `{thumbprint, canonical}` → `{signature}`; `409 locked` if the PIN has not been entered |
| `GET` | `/unlock` | PIN page, served by the agent |
| `POST` | `/v1/lock` | forget the PIN now |
| `POST` | `/v1/pair` | `{code}` — origin taken from the request |

## Token handling

Both supported tokens advertise `CKM_SHA256_RSA_PKCS`, so the token hashes and
signs the `SignedAttrs` in one call and there is no `DigestInfo` to assemble.

Modules probed (all that exist are loaded, since a machine may hold either
token):

| | Windows | macOS |
|---|---|---|
| Feitian ePass2003 | `eps2003csp11.dll`, `eps2003csp1164.dll` | `libcastle.1.0.0.dylib` |
| WatchData PROXKey | `SignatureP11.dll`, `wdpkcs.dll` | `libwdpkcs_Proxkey.dylib` |

Add anything unusual to `ExtraModules` in `agent-config.json`.

**The PIN is never retried.** Neither token reports `CKF_USER_PIN_FINAL_TRY`, so
nothing warns before one locks permanently — a retry loop would eventually brick
a taxpayer's e-seal. One `C_Login` per PIN the operator actually typed.

All token work is serialised through a single lock: vendor modules are often not
thread-safe and an HTTP server is concurrent by nature. Sessions are opened,
used, and closed immediately rather than held.

## Not done yet

- **Nothing here has been compiled.** There is no .NET SDK on the ekPOS server
  and the tokens live on a workstation, so every build and every run has to
  happen there.
- Windows only. The macOS module paths in `AgentConfig` came from vendor
  documentation, never from a machine with a token in it, and the csproj now
  pins `win-x64`.
- The installer is unsigned — see *Releasing*.
- PROXKey signing is untested; only enumeration has been exercised on it.
- No auto-update: a new version means running the installer again.
