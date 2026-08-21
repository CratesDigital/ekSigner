---
name: Bug report
about: Something does not work
title: ''
labels: bug
---

**What happened, and what you expected instead**

**ekSigner version**
<!-- Shown on http://127.0.0.1:8420/, or in Apps & features. -->

**Windows version, and token model if relevant**

**If a call failed**
<!-- The endpoint, the status code, and the JSON body if there was one.

     Before filing: a call that fails with a network error rather than a status
     code is usually one of the two documented in docs/api.md — a missing
     Access-Control-Allow-Private-Network header, or an unpaired Origin. Both
     look exactly like the agent not running. -->

**If ETA rejected a signed document**
<!-- The ETA error code and message. If you can, the signature run through
     `openssl asn1parse` — a structural problem is a bug here, a data problem
     usually is not. -->
