; PaperKnife for Windows installer (Inno Setup 6).
;
; One installer for every 64-bit Windows PC: it contains the x64 and Arm64 builds and installs
; the one that matches the PC. It installs for the current user only, without administrator
; rights, into %LOCALAPPDATA%\Programs\PaperKnife.
;
; Built by build\package.ps1, which passes:
;   /DAppVersion=<version> /DFileVersion=<numeric version>
;   /DX64Dir=<x64 publish folder> /DArm64Dir=<arm64 publish folder>

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0"
#endif
#ifndef X64Dir
  #error Pass /DX64Dir=<folder with the published win-x64 app>
#endif
#ifndef Arm64Dir
  #error Pass /DArm64Dir=<folder with the published win-arm64 app>
#endif

#define AppName "PaperKnife"
#define AppExe "PaperKnife.exe"
#define ProgId "PaperKnife.pdf"
#define RepoUrl "https://github.com/shivam098/paperknife-windows"

[Setup]
; Never change AppId: it identifies the installed app for upgrades and uninstalling.
AppId={{F880A366-2AF0-4D28-AA41-92CCE1938ABF}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=The PaperKnife Authors
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (c) The PaperKnife Authors. Licensed under the Apache License 2.0.
VersionInfoVersion={#FileVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
; x64compatible also covers Arm64 Windows 11; arm64 adds Arm64 Windows 10.
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\src\PaperKnife.App\Assets\PaperKnife.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ChangesAssociations=yes
CloseApplications=yes
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputBaseFilename={#AppName}-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#X64Dir}\*"; Excludes: "\Fonts\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64
Source: "{#Arm64Dir}\*"; Excludes: "\Fonts\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
; The fonts are the same for every kind of PC, so the installer holds one copy.
Source: "{#X64Dir}\Fonts\*"; DestDir: "{app}\Fonts"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Edit PDFs on this device"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Offer PaperKnife in "Open with" for PDF files without changing the default PDF app.
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "PDF document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
; List PaperKnife under Settings > Apps > Default apps, so people can choose it for PDFs.
Root: HKA; Subkey: "Software\{#AppName}"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Edit PDFs on this device. Your files are never uploaded."
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue
; Let "paperknife" start the app from the Run dialog.
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
