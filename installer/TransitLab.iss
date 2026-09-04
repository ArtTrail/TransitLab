; TransitLab Windows installer (Inno Setup).
;
; Per-user install (no admin/UAC needed) into {localappdata}\TransitLab — deliberately separate
; from the app's own user-data folder (%AppData%\TransitLab, i.e. Roaming — config.json,
; history.json, session logs), which lives elsewhere and is never touched by install or
; uninstall. AppId is a fixed GUID so future versions upgrade in place instead of installing
; side-by-side — this also underpins the self-update feature (a downloaded installer run with
; /VERYSILENT reinstalls over the existing install rather than creating a duplicate).
;
; This is an additional distribution option alongside the existing portable win-x64 zip, not a
; replacement for it — both are built from the same publish\win-x64 output.
;
; Build: requires publish\win-x64\ to already exist (dotnet publish -c Release -r win-x64
; --self-contained true -o publish\win-x64), then run from the installer\ directory:
;   "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" TransitLab.iss
;
; MyAppVersion is NOT read from AppInfo.cs automatically — bump it here by hand alongside
; AppInfo.Version on every release, same as every other version-stamped location in this repo.

#define MyAppName "TransitLab"
#define MyAppVersion "2.10.0"
#define MyAppPublisher "Art Trail"
#define MyAppURL "https://github.com/ArtTrail/TransitLab"
#define MyAppExeName "TransitLab.exe"

[Setup]
AppId={{A02ECB23-31B0-44D4-9DAF-5F4DED3CE8E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\publish
OutputBaseFilename=TransitLab-Setup-v{#MyAppVersion}
SetupIconFile=..\Assets\TransitLab.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
