; Wrench Downloader 26.4.1 - Inno Setup 6 installer (Rust / GPUI Kit build).
;
; Stage first:
;   artifacts\stage\26.4.1\wrench-downloader.exe
;   artifacts\stage\26.4.1\Languages\*.json
;   artifacts\stage\26.4.1\chrome extension\*
; Then compile:
;   ISCC.exe /DMyAppVersion=26.4.1 installer\app.iss

#ifndef MyAppVersion
  #define MyAppVersion "26.4.1"
#endif
#define MyAppName "Wrench Downloader"
#define MyAppExe "wrench-downloader.exe"
#define MyAppPublisher "Wrench Softwares"
#define MyAppURL "https://github.com/wrenchsoftwares/wrench-downloader"
#define StageDir "..\artifacts\stage\" + MyAppVersion

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
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=..\artifacts\releases\{#MyAppVersion}
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
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Wrench Downloader"; Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\Wrench Downloader"; Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Registry]
; Optional Start-with-Windows entry; the app starts hidden with its tray icon.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WrenchDownloader"; ValueData: """{app}\{#MyAppExe}"" --background"; Tasks: startup; Flags: uninsdeletevalue
; Custom protocol lets the browser companion launch the app when it is offline.
Root: HKCR; Subkey: "wrench"; ValueType: string; ValueName: ""; ValueData: "URL:Wrench Downloader"; Flags: uninsdeletekey
Root: HKCR; Subkey: "wrench"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCR; Subkey: "wrench\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"" ""%1"""
; Magnet-link ownership (magnet-button clicks open Wrench Downloader).
Root: HKCR; Subkey: "magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Protocol"; Flags: uninsdeletekey
Root: HKCR; Subkey: "magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCR; Subkey: "magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,Wrench Downloader}"; Flags: nowait postinstall skipifsilent
