#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
[Setup]
AppId={{324772BB-5EE5-4596-9727-07B665BEDD02}
AppName=Fire User Connect
AppVersion={#AppVersion}
AppPublisher=Fire User
DefaultDirName={localappdata}\Programs\Fire User Connect
DefaultGroupName=Fire User Connect
PrivilegesRequired=lowest
ArchitecturesAllowed={#AllowedArchitectures}
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion={#MinimumWindows}
OutputDir={#InstallerOutput}
OutputBaseFilename=FireUserConnect-{#AppVersion}-{#PackageTarget}-Setup
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\Fire User Connect.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Fire User Connect"; Filename: "{app}\Fire User Connect.exe"; AppUserModelID: "UserIdentityDesktop.Desktop"
Name: "{autodesktop}\Fire User Connect"; Filename: "{app}\Fire User Connect.exe"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "Software\Classes\fireuserconnect"; ValueType: string; ValueData: "URL:Fire User Connect"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\fireuserconnect"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\fireuserconnect\DefaultIcon"; ValueType: string; ValueData: "{app}\Fire User Connect.exe,0"
Root: HKCU; Subkey: "Software\Classes\fireuserconnect\shell\open\command"; ValueType: string; ValueData: """{app}\Fire User Connect.exe"" --open-chat ""%1"""
[Run]
Filename: "{app}\Fire User Connect.exe"; Description: "Open Fire User Connect"; Flags: nowait postinstall skipifsilent
