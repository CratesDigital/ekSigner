# ekSigner

**A signing agent for Egyptian e-invoicing.** It runs on the machine holding the
e-seal token, reads the vendor PKCS#11 module directly, and signs ETA documents
on request from whichever applications its operator has paired it with.

If you are building software that submits to the Egyptian Tax Authority, this
solves the part that has no official answer: **how does your application get a
document sealed by a token that is plugged into someone else's computer?**

[العربية](README.ar.md) · [HTTP API](docs/api.md) · [Design notes](docs/design.md) · [Security](SECURITY.md)

## The problem it solves

ETA requires every document to carry a CAdES-BES seal produced by a hardware
e-seal token. The token is physically in a shop, an accountant's office, a
warehouse — not on your server. So your application cannot sign; only the
operator's machine can.

ITIDA publishes a Web-Sign Client, but it is a **portal companion, not an
integration API**:

- its own manual scopes it to signing "through portal"
- it is launched per-signature through the `signsrv:` protocol handler, rather
  than listening on a port your application can call
- it prompts for the PIN on **every single signature**, which is unusable for a
  point-of-sale terminal issuing invoices all day
- it is Windows-only, and requires a self-signed certificate to be installed and
  accepted before a browser will talk to it

ekSigner is a small local HTTP service instead. Your web application calls
`http://127.0.0.1:8420` from the operator's browser, and gets a signature back.
The PIN is entered once per session, on a page the agent itself serves, and
never reaches your servers.

## The finding that will save you a week

ITIDA's *Digital Signature Format for E-Invoice System* v1.1 requires the CMS
`eContentType` to be **`digestedData`** (`1.2.840.113549.1.7.5`) — not the
`id-data` that every generic CAdES tool emits.

This means **`openssl cms -sign -cades` cannot produce a signature ETA accepts.**
It adds the correct `signing-certificate-v2` attribute and then always writes
`id-data`, and ETA rejects the result with an error that does not mention the
content type at all.

`EtaCades.cs` assembles the structure from BouncyCastle ASN.1 types for exactly
this reason: the high-level `CmsSignedDataGenerator` gives you no way to override
the content type, and no way to delegate the RSA operation to a smart token.
[The full structure is documented in `docs/design.md`](docs/design.md), field by
field, verified against openssl as an independent parser on all eleven
structural checks.

**This is proven against production ETA**, not just against a specification: a
document sealed this way was accepted by the preprod portal, which rendered
*Signed By: ايكتر للبرمجه والتوريدات* against the certificate.

## Supported tokens

Verified on **ePass2003** and **PROXKey**, which are what ETA-registered
taxpayers are issued in practice. Any PKCS#11 module should work — extra module
paths can be added to the config file without a rebuild.

Windows x64. The macOS PKCS#11 paths in `AgentConfig.cs` came from vendor
documentation rather than from a machine with a token in it, so treat macOS as
untested rather than supported.

## Set-up, once per workstation

1. **Install.** Download `ekSigner-Setup.exe` from
   [Releases](https://github.com/CratesDigital/ekSigner/releases) and run it. No
   administrator rights and nothing to configure; it installs under the user's
   own profile and starts at every login from then on.
2. **Pair it.** The agent's page opens by itself after installing, showing a
   six-digit code. Enter that code in the application you are pairing with. The
   agent records that origin and refuses every other one.
   To see the code again later, open <http://127.0.0.1:8420/>.
3. **Unlock the token** at <http://127.0.0.1:8420/unlock> and enter the PIN.
   It stays unlocked for 60 minutes by default.
4. **Sign**, from the application you paired.

One install signs for every site paired with it — an office running two
different systems pairs one agent with both.

> **Windows will warn you the publisher is unrecognised.** Code signing is in
> progress; until it lands, verify the download against the SHA-256 published on
> the release. See [Code signing](#code-signing).

## Integrating your own application

Three endpoints and a pairing handshake. **[Full contract in `docs/api.md`](docs/api.md).**

```
GET  /v1/ping           is the agent there, is it unlocked, are we paired
POST /v1/pair           exchange the six-digit code for a paired origin
GET  /v1/certificates   list the e-seals on the token
POST /v1/sign           canonical string in, CAdES-BES signature out
POST /v1/unlock         PIN entry (normally done on the agent's own page)
POST /v1/lock           forget the session
```

Two things bite integrators, both documented in `docs/api.md` and both producing
a failure that looks exactly like *the agent is not running*:

- **Chrome's Private Network Access.** A public → loopback request sends a
  preflight carrying `Access-Control-Request-Private-Network: true`, and the call
  fails unless the response answers `Access-Control-Allow-Private-Network: true`.
- **You must send an `Origin` header**, and pair it first. Every endpoint except
  `/v1/ping` and `/v1/pair` checks it. Pairing is rate-limited — five wrong codes
  and it answers `429` for five minutes.

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

## Where the config lives

`%APPDATA%\ekSigner\agent-config.json` — the pairing, the pairing code, the port,
and any extra PKCS#11 module paths. Deliberately *not* beside the executable:
the installer overwrites its own directory on upgrade, which would cost the
operator their pairing every time the agent updated. Configs left at either of
two earlier locations are carried across on first run.

## Releasing

```powershell
git tag v1.4.0
git push origin v1.4.0
```

`.github/workflows/release.yml` publishes self-contained for `win-x64`, compiles
`installer/eksigner.iss`, and attaches the installer to a GitHub release. It
needs a Windows runner — both the publish and Inno Setup do.

After the release is built, submit the installer to
<https://www.microsoft.com/en-us/wdsi/filesubmission> as a software-developer
false positive. Defender's heuristics flag new unsigned installers, and the
clearance is scoped to the file hash, so this repeats every release. Allow 1–3
days before pointing anyone at the download.

## Code signing

Code signing is in progress. Until it lands, Windows will warn that the
publisher is unrecognised, so **verify the installer against the SHA-256
published with each [release](https://github.com/CratesDigital/ekSigner/releases)**
before running it.

## Licence

[Apache-2.0](LICENSE). Copyright 2026 Eickter Software & Supplies.

Not affiliated with, endorsed by, or supported by the Egyptian Tax Authority or
ITIDA. It implements their published specification; it is not their software.
