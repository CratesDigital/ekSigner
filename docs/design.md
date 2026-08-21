# ekSigner — design

Status: **built and shipping.** Supersedes the browser → ITIDA Web-Sign
approach that calling applications used before it.

**The agent is not ekPOS's.** It is named ekSigner rather than for one product
because it signs for whichever sites its operator pairs it with: `PairedOrigins`
is a list, and an agency running both ekPOS and AGNC pairs one installed agent
with both and signs from either. Nothing in the protocol below is
product-specific — a caller needs the origin pairing and the three endpoints, and
that is all.

> **The signing path is proven.** A PowerShell spike signed a
> real serializer output on a real ePass2003 and produced a CAdES-BES that
> matches ITIDA's Digital Signature Format v1.1 on all eleven structural
> checks, verified with openssl as an independent parser: version 3,
> `digestedData` content type, detached `eContent`, all four signed attributes,
> `sha256WithRSAEncryption`, signer certificate only, no unsigned attributes.
>
> **ETA has now accepted a seal produced this way.** Invoice INV00021 (longId
> `5Z67NJ4M0VP7CK5RTK13DGZK10`) was built by ekPOS, sealed with the ePass2003
> e-seal through the spike, and submitted to preprod on 8 Aug 2026. The portal
> renders **"Signed By: ايكتر للبرمجه والتوريدات"** — the signature verified
> against the certificate. The document then failed at Step-07 on
> `CV302 ItemCode … does not exist in CodeType [EGS]`, because the item code
> was still awaiting approval; that is a data problem, well past signature
> validation.
>
> This also settles the canonical serialization: a mismatch with ETA's own
> re-serialization would have failed signature verification long before step 7.

## Why the ITIDA Web-Sign Client cannot be used

The client ekPOS currently calls is a **portal companion, not an integration
API**. From ITIDA's own [user manual][manual] — note the title, "Web-Sign Client
Component **for Portal**":

> "Web-Sign Client is a desktop application installed once on the user machine,
> which is responsible for the signing operation **through portal** using smart
> token."

> "After clicking the signing button, some browsers will prompt the user to open
> Web-Sign Client. Click on **'Open signsrv'** to run the application."

Four consequences, each fatal on its own:

1. **It is not a server.** It is launched per-signature through the `signsrv:`
   protocol handler. Nothing listens on `https://localhost:60025`, which is why
   that URL refuses to connect even on a machine where portal signing works.
2. **It is interactive by design.** It renders the data for review, asks the
   operator to pick a certificate, and prompts for the token PIN *on every
   signature*. Since every ETA document now carries a seal — receipts included —
   that is one PIN prompt per receipt.
3. **It is scoped to the ETA portal.** It is not documented for third-party use
   and there is no supported contract to build against.
4. **Windows 8/10 only**, per the manual's stated requirements.

## What we build instead

A small local agent that ekPOS controls end to end: it loads the token's PKCS#11
driver, prompts for the PIN once per session, and returns a CAdES-BES signature
in exactly the shape ETA requires.

```
  ekPOS page (https://tenant.ekpos.app)
        │  fetch, CORS + Private Network Access
        ▼
  ekSigner   http://127.0.0.1:8420   (tray app, per workstation)
        │  PKCS#11  C_Login / C_Sign
        ├──────────────► eps2003csp11.dll        (Feitian ePass2003)
        └──────────────► SignatureP11.dll        (WatchData PROXKey)
                              │
                         smart token (e-seal private key — never leaves it)
```

The private key never leaves the token, the PIN never leaves the agent, and
ekPOS's servers never see either.

## Transport — the detail that decides whether this works at all

Use **`http://127.0.0.1:8420`**, not HTTPS.

Loopback addresses are *potentially trustworthy origins* under the Mixed Content
spec, so an HTTPS ekPOS page may call `http://127.0.0.1` without being blocked.
This sidesteps the self-signed-certificate dance that makes the ITIDA client so
painful to set up — there is no certificate to install or accept.

Two headers are mandatory on every response, and one is easy to miss:

| Header | Value | Why |
|---|---|---|
| `Access-Control-Allow-Origin` | the paired ekPOS origin | ordinary CORS |
| `Access-Control-Allow-Private-Network` | `true` | Chrome's Private Network Access sends `Access-Control-Request-Private-Network: true` on the preflight for public → loopback requests. Without this the call fails with a network error indistinguishable from "agent not running". |

Bind to `127.0.0.1` only — never `0.0.0.0`, which would expose a signing oracle
to the LAN.

## Security model

An agent that signs whatever it is asked, by whoever asks, is a forgery service.
Three controls:

**Pairing.** On first run the agent generates a random code and shows it in its
tray window. The operator enters it once in ekPOS's Connection tab; the agent
records that origin in its allowlist. Requests from any other `Origin` are
refused. Nothing is signable by a drive-by page.

**PIN stays local.** Entered in the agent's own native dialog, held in memory for
the session only, never transmitted, never persisted.

**Visible consent, batched sensibly.** The first signature of a session shows the
document summary and requires confirmation — mirroring the portal's review step.
Subsequent signatures in the same session proceed silently so a POS can clear a
batch, with the tray icon showing an active signing session and every signature
written to a local audit log. Session expires on a timeout, on token removal, or
on explicit lock.

## Driver initialisation

Both vendors ship a standard PKCS#11 module; the work is finding it and coping
with either being present.

Both vendors are supported on Windows and macOS, since the e-seal provider — not
the tenant — decides which token is issued.

**Feitian ePass2003** — installed by the EnterSafe driver:

| OS | Candidate paths |
|---|---|
| Windows | **`C:\Windows\System32\eps2003csp11.dll`** ← verified present<br>`C:\Windows\System32\eps2003csp1164.dll` (64-bit naming on some releases)<br>`C:\Windows\System32\DriverStore\FileRepository\eps2003csp11.inf_amd64_*\eps2003csp1164.dll`<br>EnterSafe install dir, from its uninstall registry key |
| macOS | `/usr/local/lib/libcastle.1.0.0.dylib`<br>`/usr/local/lib/libcastle_v2.1.0.0.dylib` (newer driver) |

**WatchData PROXKey** — CSP `PROXKey CSP India V3.0`:

| OS | Candidate paths |
|---|---|
| Windows | **`C:\Windows\System32\SignatureP11.dll`** ← verified present<br>`C:\Windows\System32\wdpkcs.dll`<br>Watchdata install dir, from its registry key |
| macOS | `/usr/local/lib/wdProxKeyUsbKeyTool/libwdpkcs_Proxkey.dylib` |

> Vendors move files between releases, so the agent should treat this list as
> configuration rather than constants and expose an override.

### Verified against real tokens (Aug 2026)

Probed with `pkcs11-tool -I -L` against both vendor modules on Windows:

| | ePass2003 | PROXKey |
|---|---|---|
| Module | `eps2003csp11.dll` | `SignatureP11.dll` |
| Library | EnterSafe PKCS#11 v1.20 | — |
| Token label | `FIXED MISR` | `Egypt Trust` |
| Model | ePass2003 | TimeCos/PK |
| Slots exposed | 1 (`0x1`) | 4 (`0x4001` populated, `0xe002`–`0xe004` empty) |
| PIN length | **8–255** | **6–32** |

Four things follow directly, each of which would be a bug if assumed away:

1. **Do not hardcode PIN rules.** The minimum differs between the two tokens (8
   vs 6). Read `ulMinPinLen` / `ulMaxPinLen` from `CK_TOKEN_INFO` and validate
   against that.
2. **Do not assume slot 0, or contiguous slot IDs.** PROXKey exposes four slots
   with non-sequential IDs and only the first populated. Enumerate and filter on
   token presence.
3. **Neither token reports the PIN warning flags.** `CKF_USER_PIN_COUNT_LOW`,
   `CKF_USER_PIN_FINAL_TRY` and `CKF_USER_PIN_LOCKED` are all absent from both
   tokens' flag sets — exactly the case the spec permits. This is confirmation,
   not speculation: **there is no "one try left" warning to rely on**, which is
   what makes the never-auto-retry rule non-negotiable.
4. **Exclusive access is real and observed.** `certutil -scinfo` reported the
   ePass2003 as `SCARD_STATE_INUSE` — "the card is being shared by a process" —
   and could not connect to it while another process held it. The serialised
   worker and short sessions are not defensive over-engineering.

OpenSC's own module is **not** a viable fallback: pointed at the ePass2003 it
returns `CKR_TOKEN_NOT_RECOGNIZED`, the known ATR-database mismatch
([OpenSC #2424][opensc2424]). The agent binds to vendor modules only.

### Signing mechanism and key selection (PROXKey, verified)

`pkcs11-tool -M` and `-O --login` against `SignatureP11.dll`:

- **`CKM_SHA256_RSA_PKCS` is supported** (`SHA256-RSA-PKCS`, sign/verify, 512–4096
  bit). So the agent hands the token the DER-encoded `SignedAttrs` and gets
  SHA-256 + PKCS#1 v1.5 in one `C_Sign` — no `DigestInfo` to assemble.
  `CKM_RSA_PKCS` and `CKM_SHA256` are also available as a fallback path.
- Keys are **RSA 2048**, `CKA_SIGN` true (`Usage: decrypt, sign, signRecover, unwrap`).
- **`CKA_ID` pairing works.** The certificate and its private key share
  `56:78:57:46:…`, which is the join the agent relies on.

Three quirks that would otherwise cost debugging time:

1. **`CKA_LABEL` cannot be used for display.** PROXKey leaves it empty on every
   object; ePass2003 sets it to a CryptoAPI container GUID
   (`3b19c98e-af7e-…`). Neither is meaningful to an operator, so the picker must
   parse the certificate DER for subject, issuer and expiry.
2. **There can be more private keys than certificates.** PROXKey exposes two
   private keys but one certificate — the orphan belongs to the expired
   predecessor, whose certificate PKCS#11 does not surface (CryptoAPI does).
   Enumerate **certificates first** and resolve each to its key by `CKA_ID`;
   enumerating keys first yields entries that can never be selected. ePass2003
   is clean: one certificate, one public key, one private key, one shared ID.
3. **`CKA_ALWAYS_AUTHENTICATE` is unsupported on PROXKey** and returns
   `CKR_ATTRIBUTE_TYPE_INVALID`. Treat a failed query as "no re-authentication
   required" rather than propagating the error.

### Both tokens, side by side (verified)

| | ePass2003 | PROXKey |
|---|---|---|
| `CKM_SHA256_RSA_PKCS` | **yes** | **yes** |
| Key | RSA 2048 | RSA 2048 |
| `CKA_SIGN` | yes | yes |
| Cert ↔ key via `CKA_ID` | yes, one triple | yes |
| `CKA_LABEL` | container GUID | empty |
| Objects | 1 cert, 1 pub, 1 priv | 1 cert, 2 priv |
| Private key | `never extractable` | `extractable` |

**Both tokens advertise `CKM_SHA256_RSA_PKCS`, so the agent uses it and only
it**: hand the token the DER-encoded `SignedAttrs` and it returns SHA-256 plus
PKCS#1 v1.5 in one `C_Sign`.

The `CKM_RSA_PKCS` + hand-built `DigestInfo` fallback sketched earlier is
therefore **not built**. Carrying an untested code path that can only ever run on
hardware nobody has is worse than failing loudly: if a future token does not
advertise the mechanism, report that clearly and add the path then, with that
token in hand to test against.

One difference worth passing to the certificate provider, though it does not
affect this design: the PROXKey private key is marked `extractable` (wrappable
under encryption, never readable in plaintext) where the ePass2003 key is
`never extractable`. Qualified signing keys are more usually the latter.

Initialisation sequence:

1. Probe every candidate path for both vendors. Load **all** modules found; a
   workstation may have either token, and a service bureau may have both.
2. `C_Initialize` each module, then `C_GetSlotList(tokenPresent: true)`.
3. For every slot, read the token label and enumerate `CKO_CERTIFICATE` objects,
   keeping those whose matching `CKO_PRIVATE_KEY` has `CKA_SIGN = true`.
4. Present one merged certificate list, each entry tagged with its module and
   slot so signing can route back to the right token.
5. Watch for insertion/removal (`C_WaitForSlotEvent`, or polling where the driver
   does not support it) and invalidate cached sessions when a token disappears.

Architecture must match the driver: a 64-bit agent cannot load a 32-bit DLL.
Ship x64, and fall back to launching a bundled x86 helper if only a 32-bit module
is present. On macOS ship a universal binary so Apple Silicon does not have to
run under Rosetta to match an x86_64 driver.

## PIN handling and lockout

The retry counter lives in the **token hardware**, not the driver — so we cannot
extend, reset or bypass it, and there is no counter for us to implement. That
much is settled.

What does *not* follow is that we can leave it alone. PKCS#11 exposes three
status flags on `C_GetTokenInfo`:

| Flag | Meaning |
|---|---|
| `CKF_USER_PIN_COUNT_LOW` | at least one wrong PIN since the last success |
| `CKF_USER_PIN_FINAL_TRY` | one attempt left before lockout |
| `CKF_USER_PIN_LOCKED` | locked; `C_Login` will fail |

The specification permits a token to **always report these as false** if it does
not support them or its security policy withholds them — and ePass2003 in
particular has a [known defect][opensc871] where an incorrect PIN raises the
wrong `CKR_` error and the token flags do not change at all.

So the flags are usable as a warning when present, never as a guarantee. The
binding rule for the agent:

**Never retry `C_Login` automatically.** One attempt per PIN the operator
actually typed — no retry on a transient error, no retry after reconnecting, no
"try again" loop anywhere. An auto-retry against a token whose warning flags do
not work is how a tenant's e-seal gets bricked, and replacing one means going
back to the certificate provider.

Read the flags where offered and surface them ("one attempt remaining") but treat
their absence as unknown rather than safe, and always show the operator how the
token responded rather than interpreting it for them.

## Certificate selection

The certificate on a token is not necessarily an organisational e-seal. The
PROXKey probed above carries a **natural-person** certificate from Egypt Trust
(`CN` = the individual's name, with `National ID` and `EmployeeID` in the
subject, chaining to `Egypt Trust CA G6` → `Egypt_RootCA_G1` / ITIDA), not a
seal bearing a tax registration number.

That is acceptable to ETA — the signature spec calls for "one of the approved
**eSeal or natural person** signature certificates in Egypt" — but only when the
named person is registered on the ETA portal as an authorised signer for that
taxpayer. It is a portal configuration question, not something the agent can
check, so it belongs in the setup documentation.

Two practical consequences for the agent:

- **Expect several certificates per token, including expired ones.** The probed
  PROXKey holds three objects: a current certificate, its expired predecessor
  (same subject), and a container with no keys at all. Filter by validity window
  and by the presence of a usable private key, and show the expiry in the
  picker so an operator cannot pick last year's certificate.
- **Do not filter on the CryptoAPI key spec.** Windows reports these keys as
  `AT_KEYEXCHANGE` with "No AT_SIGNATURE key", which is a CryptoAPI distinction
  with no PKCS#11 equivalent. Select on `CKA_SIGN` being true on the private
  key, and pair it to its certificate by `CKA_ID`.

## Concurrent access to the token

PKCS#11 modules are frequently not thread-safe, and a tenant may be signing on
the ETA portal on the same machine with the same token.

- Serialise **all** token operations through one worker; never call a module from
  two threads, even when initialised with `CKF_OS_LOCKING_OK`.
- Keep sessions short: open → login → sign the batch → logout → close. Do not
  hold a logged-in session idle.
- Take a single-instance lock at startup so two agents cannot contend.
- On `CKR_DEVICE_REMOVED`, `CKR_SESSION_HANDLE_INVALID` or `CKR_DEVICE_ERROR`,
  drop the cached session and surface the failure. Never retry blind — see the
  lockout rule above.

## API

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/ping` | agent version, loaded modules — used for the setup check |
| `GET` | `/v1/tokens` | modules, slots, token labels, PIN state |
| `GET` | `/v1/certificates` | signing certificates: thumbprint, subject, issuer, `notAfter`, token |
| `POST` | `/v1/sign` | `{ thumbprint, canonical, documentSummary }` → `{ signature }` |

### `/v1/sign` takes the canonical string, not a hash

ekPOS sends the **full canonical serialization** and lets the agent hash it. This
is deliberate:

- It removes the ambiguity that currently blocks us with the ITIDA client, where
  we cannot tell whether it signs a supplied digest or hashes its input again —
  a double hash produces a signature ETA rejects with no diagnostic.
- It lets the agent show a meaningful confirmation, as the portal does.
- `EtaDocumentSerializer` stays the single source of truth for canonical form.

An invoice canonical string is a few KB; size is not a concern.

## The CAdES-BES structure

ETA's [signature format spec][sigspec] is specific, and one requirement is
unusual enough that generic tooling gets it wrong:

- `SignedData.version` = 3; a single `SignerInfo`, version 1, `sid` =
  `IssuerAndSerialNumber`
- digest algorithm SHA-256 — `2.16.840.1.101.3.4.2.1`
- signature algorithm `sha256WithRSAEncryption` — `1.2.840.113549.1.1.11`
- `certificates` contains **only** the signer certificate
- **no** unsigned attributes
- `encapContentInfo.eContentType` = **`digestedData`, `1.2.840.113549.1.7.5`**,
  with `eContent` absent (detached)
- four signed attributes:

  | Attribute | OID | Value |
  |---|---|---|
  | ContentType | `1.2.840.113549.1.9.3` | `digestedData` |
  | MessageDigest | `1.2.840.113549.1.9.4` | SHA-256 of the canonical bytes (UTF-8) |
  | SigningTime | `1.2.840.113549.1.9.5` | machine time, UTC |
  | ESSSigningCertificateV2 | `1.2.840.113549.1.9.16.2.47` | SHA-256 of the signer certificate |

That `digestedData` content type is why **OpenSSL is not a viable signer here**.
`openssl cms -sign -cades` (available on the server, 3.0.13) does add
ESSSigningCertificateV2, but it emits `id-data` (`1.2.840.113549.1.7.1`) and
offers no way to change it.

## Implementation stack

| Concern | Choice | Note |
|---|---|---|
| Runtime | .NET 8, self-contained single file | one signed binary per platform, no runtime prerequisite |
| PKCS#11 | Pkcs11Interop | mature, MIT, portable, handles `C_Login` / `C_Sign` |
| CMS / CAdES | BouncyCastle.Cryptography | the only convenient way to force `eContentType` to `digestedData` |
| HTTP | ASP.NET Core minimal API bound to loopback | |
| UI | **Avalonia** tray + PIN dialog | cross-platform; WinForms would tie us to Windows |

Avalonia rather than WinForms is the one stack choice driven by the macOS
requirement. Everything else in the list already runs on both platforms, so a
single codebase covers Windows and macOS with no per-OS forks — only the driver
path table and the installer differ.

Prior art worth reading before writing the CMS builder:
[`eps2003csp11-interface`][priorart] (MIT, C++) does ETA CAdES-BES against
ePass2003. Too limited to depend on — it cannot select among multiple tokens and
is oriented at COM/VBA callers — but it is a working reference for the exact
structure.

## Changes on the ekPOS side

Contained, because the submission flow already separates preparing from signing:

- `_signer.blade.php` — replace the `EtaSigner` XHR client with an agent client
  (pairing, `/v1/certificates`, `/v1/sign`). The `diagnose()` split between
  unreachable and origin-refused stays useful and maps onto the agent's states.
- `EtaSubmissionService::signingPayload()` — return the canonical string
  alongside the hash. `signed_hash` and the re-binding guard are unchanged.
- `EtaSetting` — replace `signing_server_url` semantics with agent host/port, and
  add the pairing token. Certificate metadata columns already exist.
- Connection tab Step 1/3 — swap the ITIDA install text for agent download and
  pairing, reusing the QZ Tray Setup tab's shape (`public/js/ekpos-qz.js` is the
  closest existing precedent: a local agent, per-machine settings in
  `localStorage`, graceful "not running" handling).

Nothing changes in the builders, the serializer, submission history, or the
cancel/refresh paths.

## Distribution

- **Windows** — code-signed MSI per architecture, silent-install switches for
  fleet rollout.
- **macOS** — signed and notarised `.pkg`; without notarisation Gatekeeper blocks
  it and the failure looks like a broken download.

In-app update check against a version endpoint, with the agent's version exposed
through `/v1/ping` so ekPOS can warn when a workstation is behind.

## Scope decisions

| Question | Decision |
|---|---|
| Which tokens | **Both ePass2003 and PROXKey**, discovered at runtime. The e-seal provider chooses, not the tenant, so the agent cannot assume either. |
| Platforms | **Windows and macOS.** Windows is the bulk; macOS is a small minority but both vendors ship drivers for it, so one Avalonia codebase serves both. Linux is out of scope. |
| PIN lockout | Enforced by the token; we implement no counter, but never auto-retry `C_Login` and never trust the warning flags to be present. See above. |
| Concurrency | Single serialised worker, short-lived sessions, single-instance lock, no blind retries. See above. |
| Where the key lives | **Operator workstation**, using the hardware tokens tenants already hold. Nothing new to procure and no change to how tenants obtain a seal. |

### Deferred: hosted signing

A hosted seal — HSM, or a CA remote-signing API — is the better answer once the
tenant count grows, because it restores unattended submission: today a queue
worker can only prepare a document, never complete one. That is a real
limitation, not a temporary rough edge, and it is worth revisiting when the
number of tenants makes per-workstation setup the bottleneck.

The agent design does not block it. `signingPayload()` already returns a
canonical string plus a hash, and `submitSigned()` already verifies the signature
binds to the document it was produced for; a hosted signer is a second
implementation behind the same two calls, not a rewrite.

## Remaining validation

Signature acceptance and canonical serialization are settled — see the note at
the top. What is still open:

1. **Signing on the PROXKey.** Only enumeration has been exercised there. It
   advertises the same mechanism and pairs cert to key by `CKA_ID` the same way,
   so no difference is expected, but "expected" is not "tested".
2. **A fully valid document.** INV00021 got as far as Step-07 and failed on an
   unapproved item code. Nothing after that step has been exercised, so the
   later validators are still unproven.
3. **The e-receipt path in its entirety.** Batch sealing, `previousUUID`
   chaining and the ERP-authenticated receipt submission have never touched a
   real endpoint.

   > **Reference implementation.** ETA publishes the receipt-side rules as a
   > .NET library, `ETA.eReceipt.IntegrationToolkit` on nuget.org. It is worth
   > decompiling (`ilspycmd`) rather than re-reading the prose docs: the SDK
   > pages are ambiguous or contradictory on exactly the points that produce
   > silent hash mismatches, while `Json.NormalizeReceipt` and
   > `ToolkitHelper.ValidateUuid` are unambiguous. Two rules were settled this
   > way after three rejected submissions:
   >
   > - The uuid is validated as
   >   `NormalizeReceipt(receipt).Replace(uuid, "")` — the receipt is
   >   normalized with the uuid *filled in*, then that value is blanked in the
   >   resulting text. Hashing with the property removed never matches.
   > - Scalars come from `JsonElement.GetRawText()`, so strings are hashed in
   >   their **escaped wire form** (`\uXXXX`, `\/`), not decoded. Also note
   >   that this normalizer renders booleans and null from `GetRawText()`
   >   too — `"true"`, `"false"`, `"null"` — where the invoice-side rule we
   >   implement uses `"True"` / `"False"` / `""`. ekPOS emits neither, but
   >   adding one would need this checked.
   >
   > The toolkit also exposes `/toolkit/uuid`, which computes the uuid for a
   > receipt. It is **self-hosted** (Docker/CLI/NuGet), not an ETA endpoint —
   > POSTing it to the ETA API host 404s. Run it locally if a future
   > disagreement needs an oracle. Receipts now go out over ETA's ERP channel — ekPOS is
   cloud-hosted software, not certified POS hardware, so it holds no device
   identity and asks tenants for none. `seller.deviceSerialNumber` is derived
   per branch (see `EtaSetting::deviceSerialForBranch()`); whether ETA accepts a
   serial it did not itself issue is one of the things preprod has to answer.
   The POS device-token path is still in `EtaClient`, unreachable unless
   `config('eta.receipt_channel')` is set to `pos`.

### A timeout does not mean the document was rejected

INV00021 timed out at 30 seconds and **was accepted anyway** — the local record
said the outcome was unknown while ETA held the document. The submit timeout is
now 120s and submissions are never retried, but the lesson stands: when a
submission times out, check the portal before doing anything else.

Reconciling that by hand is fine once; if it recurs, look up the document by
internal ID rather than re-submitting.

## Build note

The agent is a separate deliverable from this repository and **cannot be built or
tested on the ekPOS server** — there is no .NET SDK on it, and the tokens and
drivers only exist on Windows and macOS workstations. It needs its own repo, its
own CI with Windows and macOS runners for signing and notarisation, and physical
tokens of both types for testing. Plan for that before starting.

[manual]: https://www.eta.gov.eg/sites/default/files/2021-09/Web-Sign%20Client%20-%20User%20Manual_0.pdf
[sigspec]: https://www.eta.gov.eg/sites/default/files/2021-09/Digital%20Signature%20Format%20V1.1_final_0.pdf
[priorart]: https://github.com/melgmry0101b/eps2003csp11-interface
[opensc871]: https://github.com/OpenSC/OpenSC/issues/871
[opensc2424]: https://github.com/OpenSC/OpenSC/issues/2424
