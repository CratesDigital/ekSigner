---
name: Token report
about: Tell us whether ekSigner works with your PKCS#11 token
title: 'Token: '
labels: token-report
---

**Token model and vendor**
<!-- e.g. ePass2003 (Feitian), PROXKey (Watchdata) -->

**Did the agent find it?**
<!-- Open http://127.0.0.1:8420/ — the status page lists the loaded modules. -->

- [ ] The module loaded and certificates were listed
- [ ] The module loaded but no certificates appeared
- [ ] The module was not found at all

**Module path**
<!-- The DLL, e.g. C:\Windows\System32\eps2003csp11.dll. If you had to add it
     manually to agent-config.json, say so — that path is worth building in. -->

**Windows version**

**Did signing work end to end?**
<!-- Did ETA accept a document sealed with it? Which environment, preprod or
     production? -->
