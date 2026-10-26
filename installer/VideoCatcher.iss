; Built by .github/workflows/release.yml: ISCC /DAppVersion=x.y.z installer\VideoCatcher.iss
#define AppName "VideoCatcher"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8F3C2A71-5B4E-4D2A-9C61-3E7F0B9A4D12}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Oliver Huang
; Per-user install: no admin prompt, {autopf} resolves to %LocalAppData%\Programs.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=VideoCatcher-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\VideoCatcher.exe

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VideoCatcher.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VideoCatcher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VideoCatcher.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
