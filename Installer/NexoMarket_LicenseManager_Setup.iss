#define MyAppName "NexoMarket License Manager"
#define MyAppVersion "1.0"
#define MyAppPublisher "NexoMarket"
#define MyAppExeName "NexoMarket License Manager.exe"

[Setup]
AppId={{9A1B4A10-4A9E-4E4E-9E7A-7D7B9A9C1101}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\NexoMarket License Manager
DefaultGroupName=NexoMarket
OutputDir=Output
OutputBaseFilename=NexoMarket_LicenseManager_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=classic
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\NexoMarket.LicenseManager\SALIDA\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NexoMarket License Manager"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\NexoMarket License Manager"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir NexoMarket License Manager"; Flags: nowait postinstall skipifsilent
