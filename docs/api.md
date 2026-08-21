# ekSigner HTTP API

The agent listens on `http://127.0.0.1:8420` (configurable) on the operator's own
machine. Your application calls it **from the operator's browser** — your server
can never reach it, and should not try.

All responses are JSON with an `ok` boolean, except `GET /` and `GET /unlock`,
which serve HTML pages the agent renders itself.

---

## Before you write any code: two things that will waste your afternoon

### 1. Chrome's Private Network Access

A page served from a public origin calling `http://127.0.0.1` triggers a
preflight carrying `Access-Control-Request-Private-Network: true`. Chrome
**refuses the request** unless the response answers
`Access-Control-Allow-Private-Network: true`.

ekSigner sends that header on every response. You do not have to do anything —
but if you fork the agent and drop it, every call fails with a network error that
is **indistinguishable from the agent not being installed**, and you will spend
the afternoon debugging the wrong thing.

### 2. Plain HTTP is correct here — do not "fix" it

Loopback is a [potentially trustworthy origin](https://w3c.github.io/webappsec-secure-contexts/),
so an **HTTPS** page may call `http://127.0.0.1` without mixed-content blocking.
This is deliberate and it is what makes ekSigner easy to install: there is no
certificate to generate, install or accept, which is the step that makes the
ITIDA client painful.

---

## Pairing

The agent refuses to touch the token for a web origin it has not been paired
with. Pairing is a one-time, human-in-the-loop exchange:

1. The operator opens <http://127.0.0.1:8420/> and reads a **six-digit code**
   (CSPRNG-generated, stored in the config file).
2. Your application shows a box for that code and `POST`s it to `/v1/pair`.
3. The agent records the `Origin` header of that request and allows it from then
   on.

The code proves the person configuring your site is sitting at the machine with
the token. It is not a secret shared with your server, and it does not need to
be.

**Send an `Origin` header.** Browsers do this automatically. Everything except
`/v1/ping` and `/v1/pair` requires the origin to be paired — `/v1/certificates`,
`/v1/sign`, `/v1/unlock` and `/v1/lock`. Requests with *no* `Origin` at all
(curl) are treated as same-machine and allowed through; the browser is the
untrusted caller here, not the shell. The agent's own pages are recognised by
their loopback origin.

---

## Endpoints

### `GET /v1/ping`

Discovery and state. **No pairing required** — a site has to be able to ask
whether it is paired before it is.

```json
{
  "ok": true,
  "agent": "eksigner",
  "version": "1.4.0.0",
  "modules": ["C:\\Windows\\System32\\eps2003csp11.dll"],
  "unlocked": true,
  "unlocked_until": "2026-08-21T15:42:00Z",
  "paired": true
}
```

| Field | Meaning |
|---|---|
| `modules` | PKCS#11 modules actually loaded. Empty means no token driver is installed. |
| `unlocked` | Whether the PIN session is live. |
| `unlocked_until` | When it expires, or `null` if locked. |
| `paired` | Whether **this caller's** origin is paired. Use it to decide between showing the pairing box and showing the signing UI. |

Keep the timeout short. A browser that cannot connect should conclude quickly
that no agent is running, rather than hanging your page.

### `POST /v1/pair`

```json
{ "code": "418306" }
```

**200** `{ "ok": true, "origin": "https://your-app.example" }`
**400** no `Origin` header — pairing must be initiated from the site being paired.
**403** wrong code.
**429** too many wrong codes:

```json
{
  "ok": false,
  "error": "too_many_attempts",
  "retry_after_seconds": 300,
  "message": "Too many incorrect pairing codes. Try again in 5 minute(s)."
}
```

Five wrong codes and pairing is refused for five minutes. Show
`message` and disable the button for `retry_after_seconds`; do not retry
automatically, and do not offer to "try all codes". The limit exists because six
digits is only a secret while guessing is expensive.

### `GET /v1/certificates`

Lists the e-seals on the token. Requires pairing. Needs **no PIN** — certificates
are public objects on a PKCS#11 token.

```json
{
  "ok": true,
  "certificates": [
    {
      "thumbprint": "9F2C…",
      "token": "ePass2003",
      "subject": "CN=…, O=…",
      "issuer": "CN=Egypt Trust Class 3 …",
      "not_after": "2027-03-14",
      "expired": false
    }
  ]
}
```

Sorted with the latest expiry first. Store the `thumbprint` — it is what you pass
back to `/v1/sign`.

### `POST /v1/sign`

```json
{
  "thumbprint": "9F2C…",
  "canonical": "\"documentType\"\"I\"\"documentTypeVersion\"…",
  "summary": "INV00021"
}
```

`canonical` is the **ETA canonical serialization of the whole document**, as a
string. Send the string, not a hash: the agent hashes it itself, which is what
removes any question of the data being digested twice. `summary` is optional and
used only for display.

**200** `{ "ok": true, "signature": "MIIG…" }` — base64 of the DER-encoded CMS
`SignedData`, detached, ready to drop into the document's `signatures[0].value`.

**409** the token is locked:

```json
{
  "ok": false,
  "error": "locked",
  "unlock_url": "http://127.0.0.1:8420/unlock",
  "message": "The signing token is locked. Unlock it and try again."
}
```

Handle this one properly — it is the common case, not an edge case, because the
unlock expires every 60 minutes. Send the operator to `unlock_url` and let them
retry.

**400** missing `thumbprint` or `canonical`. **403** not paired.
**500** anything else, with `message` carrying the token's own error text —
including an expired certificate, which is refused before it is used.

### `POST /v1/unlock`

Requires pairing.

```json
{ "pin": "……" }
```

**200** `{ "ok": true, "until": "2026-08-21T15:42:00Z" }`, **400** with the
token's message otherwise, **403** if not paired.

**Prefer sending the operator to `GET /unlock` instead of building your own PIN
box.** That page is served by the agent, so the PIN never touches your page, your
JavaScript, or your servers — which is the property you want to be able to state
plainly to a customer handing over their e-seal PIN.

The PIN is tried **once, exactly as typed, and never retried.** Neither supported
token reports how many attempts remain, and a retry loop can permanently lock a
taxpayer's e-seal. Do not build a retry loop on top of this endpoint either.

### `POST /v1/lock`

Forgets the PIN session immediately. Requires pairing. `{ "ok": true }`.

### `GET /` and `GET /unlock`

HTML pages the agent serves for the operator: the status page (pairing code,
paired sites, token state) and the PIN page. Link to them; do not scrape them.

---

## A minimal integration

```js
const AGENT = 'http://127.0.0.1:8420';

// 1. Is it there, and does it know us?
const ping = await fetch(`${AGENT}/v1/ping`, { signal: AbortSignal.timeout(2000) })
  .then(r => r.json())
  .catch(() => null);

if (!ping)        return showInstallPrompt();
if (!ping.paired) return showPairingBox();      // POST /v1/pair with the code
if (!ping.unlocked) return showUnlockLink(`${AGENT}/unlock`);

// 2. Sign.
const res = await fetch(`${AGENT}/v1/sign`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ thumbprint, canonical, summary: documentName }),
});
const out = await res.json();

if (res.status === 409) return showUnlockLink(out.unlock_url);  // expired mid-session
if (!out.ok)            return showError(out.message);

submitToEta(out.signature);
```

Note the two states worth handling separately: **no agent** (offer the installer)
and **locked** (offer the unlock link). Collapsing them into one "signing failed"
message is the difference between an operator who knows what to do and a support
call.

---

## Signature structure

If you are implementing the CAdES yourself rather than using the agent, read
[`design.md`](design.md) first, and note the single detail that breaks generic
tooling: ETA requires `eContentType` = `digestedData` (`1.2.840.113549.1.7.5`),
not `id-data`. `openssl cms -sign -cades` always emits `id-data` and its output
is rejected.
