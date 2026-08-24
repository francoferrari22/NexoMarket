#define MyAppName "NexoMarket"
#define MyAppVersion "4.1"
#define MyAppPublisher "NexoMarket"
#define MyAppExeName "NexoMarket.Admin.exe"

[Setup]
AppId={{C0A9A6B4-4E31-4D3E-A5C8-0F7A5C9B8E11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\NexoMarket Admin
DefaultGroupName=NexoMarket
OutputDir=Output
OutputBaseFilename=NexoMarket_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=classic
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\NexoMarket.Admin\SALIDA\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\NexoMarket.LicenseManager\SALIDA\*"; DestDir: "{app}\LicenseManager"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NexoMarket Admin"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\NexoMarket Admin"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\NexoMarket License Manager"; Filename: "{app}\LicenseManager\NexoMarket License Manager.exe"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir NexoMarket Admin"; Flags: nowait postinstall skipifsilent
