; Network-ATC installer (Inno Setup 6).
; Built by .github/workflows/release.yml:
;   iscc /DAppVersion=1.0.0 /DSourceDir=..\publish\Network-ATC /DOutputDir=..\dist installer\Network-ATC.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\Network-ATC"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Network-ATC"
#define AppExe "Network-ATC.exe"

[Setup]
AppId={{AE7534AC-A0BD-4205-95DB-CE1FF4098EE8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SkyNetwork
AppPublisherURL=https://github.com/Anntixs/Network-ATC
AppSupportURL=https://github.com/Anntixs/Network-ATC/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\SkyNetwork\{#AppName}
DefaultGroupName=SkyNetwork
DisableProgramGroupPage=yes
; Installs for all users (asks for administrator rights) or, if chosen, only for the current user.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Profiles and settings in the user profile are kept on uninstall.
