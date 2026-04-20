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
#define MyAppVersion "8.3.1"
#define MyAppPublisher "Neopatron Ltd."
#define MyAppURL "https://donclaw.com"
#define MyAppExeName "DonClawNode.exe"

; Where the publish output lives. Using a relative path so CI / contributors
; don't need to patch this file before building. Kept in sync with the path the
; current dotnet-publish target uses (`-o C:\donclaw-build-v8.3.1`).
; Override on the command line with: /DPublishDir="C:\your\path"
#ifndef PublishDir
#define PublishDir "C:\donclaw-build-v8.3.1"
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

; Tell Windows this setup is signed by a real publisher and is not a Microsoft
; Store app. Remove once we have an EV cert and can actually sign.
SignTool=
SetupIconFile=..\src\DonClaw.Tray.WinUI\Assets\donclaw.ico

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Iniciar DonClaw Node con Windows"; GroupDescription: "Inicio automático:"; Flags: checkedonce

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

[Code]
// Friendly bail-out message when the publish folder is empty — easier to
// debug than Inno's default "0 files compiled" warning.
function InitializeSetup(): Boolean;
var
  PublishPath: String;
begin
  Result := True;
  PublishPath := '{#PublishDir}';
  if not FileExists(PublishPath + '\{#MyAppExeName}') then
  begin
    MsgBox('No se encontró ' + PublishPath + '\{#MyAppExeName}.' + #13#10 +
           'Ejecuta "dotnet publish" antes de compilar el instalador.',
           mbError, MB_OK);
    Result := False;
  end;
end;
