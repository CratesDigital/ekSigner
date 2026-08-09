; Inno Setup script for the ekPOS Signing Agent.
;
; Build the payload first, from tools/eta-sign-agent:
;     dotnet publish -c Release
; then compile this script:
;     iscc installer\ekpos-sign-agent.iss
;
; Produces one ekPOS-Signing-Agent-Setup.exe: double-click, Install, Finish.
;
; Two choices are deliberate and worth not undoing:
;
; PER-USER, NOT A SERVICE. A PKCS#11 token is bound to the interactive
; user's session. A machine-wide Windows service would load the vendor module
; happily and then find no token in it, which is a confusing way to fail. So
; the agent installs under the user's own profile and starts from Startup,
; which also means no UAC prompt during install — the tenants running this are
; shop staff, not administrators.
;
; NO FIREWALL RULE. The agent binds 127.0.0.1 only. Windows Firewall does not
; filter loopback and does not prompt for it. Anything here that opened a port
; would be turning a signing agent into a signing service for the LAN.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName    "ekPOS Signing Agent"
#define Publisher  "Eickter Software & Supplies"
#define ExeName    "ekpos-sign-agent.exe"
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; Never change AppId — it is how Windows recognises an upgrade rather than a
; second, parallel installation.
AppId={{8F3C1A72-6D4E-4B58-9E2A-1C7F5D0B3A94}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://eickter.com
AppSupportURL=mailto:info@eickter.com
AppCopyright=© 2026 Eickter Software & Supplies
; Stamped into the setup executable itself, so the installer a tenant downloads
; names its publisher in Properties → Details rather than showing a blank pane.
VersionInfoCompany={#Publisher}
VersionInfoProductName={#AppName}
VersionInfoDescription=ekPOS Signing Agent Setup
VersionInfoCopyright=© 2026 Eickter Software & Supplies
VersionInfoVersion={#AppVersion}
SetupIconFile=..\ekpos.ico
DefaultDirName={localappdata}\Programs\ekPOS Signing Agent
DefaultGroupName={#AppName}
OutputDir=..\dist
OutputBaseFilename=ekPOS-Signing-Agent-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Nothing to choose: keep it to Install then Finish.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#ExeName}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"

[Files]
; The whole publish folder rather than the single exe by name: whether
; self-contained publish emits one file or a handful depends on the SDK
; version, and a missing runtime DLL fails at launch with nothing to read.
; Excludes are belt-and-braces: the csproj already suppresses web.config, but
; the IIS shim is emitted by the Web SDK's own targets and an SDK update could
; put either back. Neither belongs in a desktop install.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,web.config,aspnetcorev2_inprocess.dll"

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#ExeName}"
Name: "{group}\Signing agent page"; Filename: "http://127.0.0.1:8420/"
; No {userstartup} shortcut. The agent manages autostart itself through the
; HKCU Run key so the operator can turn it off from the tray menu — and having
; both mechanisms is what made the agent always already running, so that every
; manual launch became a second instance and raised an error dialog.

[InstallDelete]
; Remove the shortcut installed by 1.1.0 and earlier, now that the Run key has
; taken over. Left behind it would start a second copy at every login.
Type: files; Name: "{userstartup}\{#AppName}.lnk"

[Registry]
; The agent writes this itself, so nothing is created here. It is declared only
; so uninstalling takes it away — left behind, Windows would try to launch a
; deleted executable at every login.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "ekPOS Signing Agent"; ValueType: none; Flags: uninsdeletevalue

[Run]
; First run opens the agent's page by itself, which is where the pairing code
; is, so there is nothing to explain on the Finish screen.
Filename: "{app}\{#ExeName}"; Description: "Start the signing agent"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#ExeName}"; Flags: runhidden; RunOnceId: "StopAgent"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // An upgrade almost always runs while the old agent is up — it started at
  // login. Its exe is locked, and Inno's "close these applications" prompt
  // cannot find it, because a windowless process has no window to close.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#ExeName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

// agent-config.json lives in %APPDATA%\ekPOS and is left alone on uninstall,
// so reinstalling keeps the pairing and the chosen certificate.
