# Changelog

Notable changes to ekSigner. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases before 1.4.0 were made while the agent lived inside a closed
application's repository; their entries are reconstructed from that history.

## [Unreleased]

### Added
- Apache-2.0 licence, `NOTICE`, and per-file licence headers.
- `docs/api.md` — the HTTP contract, for applications integrating the agent.
- `SECURITY.md`, stating the threat model and, explicitly, its known gaps.
- Arabic README.

### Changed
- **The build no longer publishes a self-unpacking single file.** Self-contained
  plus `PublishSingleFile` produced a ~70 MB executable that unpacked itself at
  startup; wrapped in an lzma2/max solid-compressed installer, the download had
  the shape of a packed dropper and Defender was deleting it on arrival rather
  than warning about it. The runtime now ships as loose DLLs. The install
  directory is larger and nothing else changes — the installer already globbed
  the whole publish folder.
- README rewritten for developers integrating the agent, rather than for one
  application's operators.

### Security
- **Other websites can no longer read the pairing code.** CORS headers were
  sent on every response, including the agent's own front page, which shows
  the pairing code — so any page the operator visited could fetch it, pair
  itself, and request seals whenever the token was unlocked. They are now sent
  only on the `/v1/` API; the front page and the unlock page carry none and
  refuse to be framed.
- **Requests not addressed to `127.0.0.1` or `localhost` are refused** with
  `421`. A site could otherwise point its own hostname at 127.0.0.1 (DNS
  rebinding) and read the agent page as same-origin, past every CORS rule.
- **`/v1/pair` is now rate-limited** — five wrong codes and pairing is refused
  for five minutes, answering `429`. Unthrottled, the six-digit code could be
  enumerated over loopback in minutes by a page left open in the operator's
  browser, after which it could poll `/v1/ping` until the token was unlocked and
  request seals under the taxpayer's e-seal.
- **`/v1/unlock` and `/v1/lock` now require a paired origin.** The PIN was always
  required, so this was never a way to unlock a token — but an unpaired page
  could submit wrong PINs in a loop, and neither supported token reports its
  remaining attempts, which makes that a way to permanently lock a taxpayer's
  e-seal from a browser tab. The agent's own pages are recognised by their
  loopback origin and are unaffected.

## [1.3.0] — 2026-08-19

### Changed
- Renamed to **ekSigner**. The agent signs for whichever sites its operator
  pairs it with, so naming it after one application was wrong: an office running
  two systems pairs one install with both.
- Every string an operator reads is product-neutral.

### Migration
The old `HKCU\...\Run` value, the old `%APPDATA%` config directory and the old
executable name are all carried over automatically. Upgrading keeps the pairing
and the chosen certificate; nobody has to pair again.

## [1.2.0] — 2026-08-09

### Added
- Notification-area icon with the token state in its tooltip, and a menu for the
  agent page, the unlock page, the run-at-login switch and quitting.
- Autostart moved to the `HKCU\...\Run` key, written by the agent rather than the
  installer, so the operator can turn it off. The Startup-folder shortcut was
  removed: having both meant the agent was always already running, and every
  manual launch raised an error dialog.

## [1.1.0] — 2026-08-09

### Fixed
- IIS hosting assets (`web.config`, the ANCM shim) no longer land in the install
  folder. The Web SDK emitted them for a desktop agent that will never sit
  behind IIS.
- A readable dialog when the agent cannot bind its port, instead of a windowless
  process exiting silently.

## [1.0.0] — 2026-08-08

### Added
- Inno Setup installer. Per-user, no administrator rights, no UAC prompt.
- The agent's own status page: pairing code, paired sites, token state. An
  installed agent has no console to print a pairing code to.
- Windowless build.

## [0.1.0] — 2026-08-08

### Added
- Initial agent: PKCS#11 token access, CAdES-BES construction to ITIDA *Digital
  Signature Format for E-Invoice System* v1.1, loopback HTTP server, origin
  pairing.
- Config stored in `%APPDATA%` rather than beside the executable, so an upgrade
  does not cost the operator their pairing.
