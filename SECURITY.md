# Security

ekSigner holds a PKCS#11 session to a hardware e-seal and listens on a port.
This document states what it defends against, what it does not, and how to
report something.

## Reporting a vulnerability

Email **info@eickter.com** with "ekSigner" in the subject. Please do not open a
public issue for anything that would let a third party obtain a signature.

We will acknowledge within a week. This is a small team and there is no bounty
programme; credit in the release notes is offered and usually taken.

## What the design defends

**The agent is not reachable from the network.** It binds `127.0.0.1`, never
`0.0.0.0`. There is deliberately no firewall rule in the installer: Windows does
not filter loopback and does not prompt for it, and anything that opened a port
would turn a signing agent into a signing *service* for the LAN.

**The PIN never leaves the machine.** It is typed into a page the agent itself
serves on loopback, so it does not pass through the calling application's page,
its JavaScript, or its servers. It is held in memory only, for the unlock window
(60 minutes by default), and is never written to the config file or to disk.

**The PIN is tried once and never retried.** Neither supported token reports how
many attempts remain, and an automatic retry can permanently brick a taxpayer's
e-seal. Integrators must not build a retry loop on top of `/v1/unlock` either.

**Web origins are allow-listed by an out-of-band code.** A site can only reach
the token after the operator has read a six-digit code off the agent's own page
and entered it on that site — which proves whoever configured the site was
sitting at the machine holding the token.

**Pairing attempts are rate-limited.** Five wrong codes and `/v1/pair` refuses
everything for five minutes. Six digits is only a secret while guessing is
expensive: unthrottled, a page left open in the operator's browser could work
through all 900,000 codes over loopback in minutes and then poll `/v1/ping` until
the token was unlocked. The cooldown puts the expected time to guess a code past
a year and a half. Deliberately *not* combined with rotating the code on
exhaustion — each guess remains 1 in 900,000 either way, so rotation buys no
security while leaving the operator looking at a code that silently stopped
working.

**Every endpoint touching the token or the session requires a paired origin** —
`/v1/certificates`, `/v1/sign`, `/v1/unlock` and `/v1/lock`. The agent's own
pages are recognised by their loopback origin, so the unlock page can still call
the endpoint it exists for. `/v1/ping` and `/v1/pair` are necessarily open: a
site has to be able to ask whether it is paired, and to pair.

**CORS is not the security boundary, and is not pretending to be.**
`Access-Control-Allow-Origin` is echoed for *every* origin, because gating it on
pairing makes pairing impossible: `/v1/pair`'s own preflight comes from an origin
that is by definition not yet paired. Access is enforced by the pairing check
inside each handler.

## What it does not defend

Stated plainly, because a signing agent that overstates its guarantees is worse
than one that does not make them.

**Any program running as the same Windows user can call the agent.** Loopback
carries no process identity, so the agent cannot tell your browser from any other
process in that session. While the token is unlocked, such a program can request
signatures. The boundary here is the user account: malware already running as the
operator is past this design, and would in any case be able to drive the token
directly through the same vendor module.

**The unlock window is time-based, not per-signature.** This is the deliberate
trade that makes the agent usable at a point of sale — the alternative is ITIDA's
client, which prompts for the PIN on every invoice. Shorten it by setting
`UnlockMinutes` in `%APPDATA%\ekSigner\agent-config.json`; set it low if the
workstation is not physically supervised.

**The config file is readable by the user who owns it.** It holds the pairing
code, the paired origins and the port. It holds no PIN and no key material — a
PKCS#11 key cannot be extracted from the token by design.

## Scope

In scope: the agent's HTTP surface, the pairing mechanism, PIN handling, the
CAdES-BES construction, and the installer.

Out of scope: vendor PKCS#11 modules and token drivers (report those to the token
vendor), and the security of any application that pairs with the agent.
