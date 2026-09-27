; Wrench Downloader — Inno Setup 6 installer script.
; Built by release-installer.ps1, which stages the published app under
; artifacts/stage/<version>/ and invokes:
;   ISCC.exe /DMyAppVersion=<version> installer/app.iss
; Installed mode is NON-portable: no portable.mode is staged, so settings,
; history and logs live in %LOCALAPPDATA%\WrenchDownloader (see PortablePaths).

#ifndef MyAppVersion
  #define MyAppVersion "26.2.2"
#endif
#define MyAppName "Wrench Downloader"
#define MyAppExe "WrenchDownloader.exe"
#define MyAppPublisher "Wrench Softwares"
#define MyAppURL "https://github.com/wrenchsoftwares/wrench-downloader"
#define StageDir "..\\artifacts\\stage\\" + MyAppVersion

[Setup]
AppId={{2C7C67D0-41E0-489A-9A43-68C38E10687B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Wrench Downloader
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64os
PrivilegesRequired=admin
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\\Assets\\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=..\\artifacts\\releases\\{#MyAppVersion}
OutputBaseFilename=WrenchDownloader-{#MyAppVersion}-Setup
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Start Wrench Downloader with Windows (minimized to tray)"; GroupDescription: "Windows integration:"

[Files]
; Everything published by `dotnet publish` plus the staged Chrome extension folder.
Source: "{#StageDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Wrench Downloader"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\Wrench Downloader"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Same Run value the app manages itself in SettingsHelper, so the installer
; checkbox and the in-app "Start with Windows" toggle never fight.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WrenchDownloader"; ValueData: """{app}\{#MyAppExe}"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,Wrench Downloader}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the staged browser-extension copy; user settings/history are kept.
Type: filesandordirs; Name: "{localappdata}\WrenchDownloader\chrome-extension"
