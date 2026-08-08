# ekPOS Signing Agent

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

## Run

```powershell
dotnet run
```

It prints a pairing code and listens on `http://127.0.0.1:8420`.

## Set-up, once per workstation

1. **Start the agent.** It shows a six-digit pairing code.
2. **Pair it** — ekPOS → Integrations → ETA → Connection → enter the code.
   The agent records that origin and will refuse every other one.
3. **Unlock the token** at <http://127.0.0.1:8420/unlock> and enter the PIN.
   It stays unlocked for 60 minutes by default.
4. **Sign** from ekPOS → Submissions.

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

**The PIN is typed into a page the agent serves.** Not into ekPOS. It never
enters ekPOS's browser context and never reaches its servers. That is also why
there is no GUI toolkit here and why the same build works on Windows and macOS.

**ekPOS sends the canonical string, never a hash.** The agent digests it itself,
so there is no question of the data being hashed twice — the ambiguity that made
the ITIDA client impossible to verify.

## Endpoints

| Method | Path | Notes |
|---|---|---|
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

- No tray icon or installer; it runs in a console window.
- Not tested on macOS — the module paths there are from vendor documentation,
  not from a machine.
- PROXKey signing is untested; only enumeration has been exercised on it.
- No auto-update.
