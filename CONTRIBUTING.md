# Contributing

Bug reports and patches are welcome, particularly from people running this
against tokens or ETA configurations we cannot test.

## What is most useful

**Reports from other tokens.** The agent is verified on ePass2003 and PROXKey.
If your PKCS#11 module loads, or fails to, say so — including the module path
and the vendor. Widening `AgentConfig.ModulePaths()` is the single easiest way
to make this useful to more people.

**ETA rejections that turn out to be signature-structural.** If ETA refuses a
document sealed by this agent and the cause was the seal rather than the data,
that is a bug and we want it. Include the error code and, if you can, the
signature run through `openssl asn1parse`.

**Integration friction.** If `docs/api.md` left you guessing, that is a
documentation bug worth filing.

## Before you open a pull request

- **Discuss protocol changes first.** Deployed agents are paired with live
  systems, and a breaking change to `/v1/*` strands them. New behaviour goes
  behind a new field or a new endpoint.
- **Do not remove the compatibility shims.** Anything named after `ekPOS` in
  `Autostart.cs`, `AgentConfig.cs` and `installer/eksigner.iss` handles installs
  from before the agent was generalised. Those machines are in service; deleting
  the shims leaves them starting two agents at login.
- **Do not add a retry loop around PIN entry.** Neither supported token reports
  the remaining attempt count, and retrying can permanently lock a taxpayer's
  e-seal.
- **Do not bind anything but `127.0.0.1`**, and do not add a firewall rule. Both
  turn a personal signing agent into a signing service for the network.
- **Keep `Access-Control-Allow-Private-Network`.** Without it every call from a
  Chrome page fails in a way indistinguishable from the agent not running.

## Style

Match the surrounding code. Comments here explain *why* a decision was made and
what breaks if it is undone — several of them are the only record of a bug that
took a day to find. Keep that habit; a comment saying what the next line does is
not the same thing.

## Building

```powershell
dotnet run                  # runs it, tray and all
dotnet publish -c Release   # what the release workflow does
```

Compiling the installer needs Inno Setup 6.3 or newer, on Windows:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\eksigner.iss
```

There is no test suite. The things worth testing — a real token, a real PIN, a
real ETA submission — are exactly the things a CI runner cannot do. Say in your
pull request what you actually exercised and on which token.

## Licence

Contributions are accepted under [Apache-2.0](LICENSE), the project's licence.
