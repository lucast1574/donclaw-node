; ==============================================================================
; DonClaw Node — Inno Setup installer script
;
; Produces a single .exe installer that drops DonClawNode.exe + its dependencies
; into Program Files, creates Start Menu entry + optional desktop shortcut, and
; registers an uninstaller visible in "Apps & features". No admin elevation is
; required to run the installed app — default integrity is sufficient for the
; Node's day-to-day workload, and the app exposes its own "Relaunch as admin"
; link in Settings for the cases that actually need it.
;
; Usage (from the repo root, on a Windows machine with Inno Setup 6+):
;
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DonClawNode.iss
;
; Output goes to .\installer\output\DonClawNode-Setup-{version}.exe
; ==============================================================================

#define MyAppName "DonClaw Node"
#define MyAppVersion "8.5.1"
#define MyAppPublisher "Neopatron Ltd."
#define MyAppURL "https://donclaw.com"
#define MyAppExeName "DonClawNode.exe"

; Where the publish output lives. Using a relative path so CI / contributors
; don't need to patch this file before building. Kept in sync with the path the
; current dotnet-publish target uses (`-o C:\donclaw-build-v8.5.1`).
; Override on the command line with: /DPublishDir="C:\your\path"
#ifndef PublishDir
#define PublishDir "C:\donclaw-build-v8.5.1"
#endif

[Setup]
; A stable AppId so upgrades replace rather than install side-by-side.
; Do NOT change this string between releases — changing it re-installs.
AppId={{B1D3C4F8-9A7E-4C42-A2DC-DC7B1F3B1A01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/docs/node
AppUpdatesURL=https://github.com/lucast1574/donclaw-node/releases

; Per-user install. Avoids triggering UAC and sidesteps the "admin installed it
; so only admin can uninstall" footgun. The tray exe itself also runs per-user
; by default.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir=output
OutputBaseFilename=DonClawNode-Setup-{#MyAppVersion}
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; Code signing: flip these on once we have a real cert. Until then Windows
; SmartScreen and Chrome's safe-browsing scare users with red warnings on
; every download — not a real security issue but a conversion killer.
;
; Easiest path today: Azure Trusted Signing (~$10/mo) gives EV-like reputation
; without managing an HSM. Once configured via `signtool` on PATH, uncomment
; both lines below and pass /SignTool=signtool to ISCC.
;
;SignTool=signtool
;SignedUninstaller=yes
SetupIconFile=..\src\DonClaw.Tray.WinUI\Assets\donclaw.ico

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Iniciar DonClaw Node con Windows"; GroupDescription: "Inicio automático:"; Flags: checkedonce

[Dirs]
; Explicit directory creation. Inno Setup will create {app} and any subdirs
; from [Files] automatically, but we also pre-create the runtime data folder
; under LocalAppData so the Node doesn't need to mkdir it on first launch.
; uninsneveruninstall: we don't want to wipe user's device identity / logs
; on upgrade, and we don't want to wipe it on full uninstall either — if
; users reinstall they keep their pairing instead of re-approving every time.
Name: "{app}"; Flags: uninsalwaysuninstall
Name: "{localappdata}\DonClawNode"; Flags: uninsneveruninstall

[Files]
; Ship everything that dotnet publish put into PublishDir. /E = recursive,
; skipIfSourceDoesntExist guards against running ISCC with a stale path.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Optional autostart. The checkbox in Tasks toggles this. Stored under HKCU so
; we don't need admin and don't leak across Windows user accounts.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "DonClawNode"; ValueData: """{app}\{#MyAppExeName}"" --silent"; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Offer to launch the Node right after install (unchecked — many users install
; then configure later via the tray icon). If launched here, pass --silent so
; we don't pop the Settings window automatically.
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar {#MyAppName} ahora"; \
  Parameters: "--silent"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
; Try to stop any running Node process before uninstall so we can delete the
; exe. taskkill is built-in on every Windows since forever; /F forces, /IM by
; image name. 'runhidden' suppresses the brief cmd window flash.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; \
  Flags: runhidden; RunOnceId: "KillNode"

; Build-time sanity check: fail the ISCC compile (not the user install!) if
; the publish folder is empty. Runs at COMPILER time via a preprocessor
; directive — previously this lived in an [Code] InitializeSetup() function
; that ran on the END-USER machine looking for the author's build path,
; which obviously wasn't there so the installer aborted on first screen.
#if !FileExists(AddBackslash(PublishDir) + MyAppExeName)
  #error The publish folder does not contain the Node exe. Run `dotnet publish` before compiling the installer.
#endif
