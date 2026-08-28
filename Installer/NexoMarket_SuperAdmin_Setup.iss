#define MyAppName "NexoMarket Super Administrador"
#define MyAppVersion "5.22"
#define MyAppPublisher "NexoMarket"
#define MyAppExeName "NexoMarket.SuperAdmin.exe"

[Setup]
AppId={{D7F3A9C2-0A4D-4C0B-9E1B-5F7E2B4A9C10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\NexoMarket Super Administrador
DefaultGroupName=NexoMarket
OutputDir=Output\SuperAdmin
OutputBaseFilename=NexoMarket_SuperAdmin_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}

[Files]
Source: "..\NexoMarket.SuperAdmin\bin\Release\NexoMarket.SuperAdmin.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NexoMarket.SuperAdmin\Assets\NexoMarket.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\NexoMarket Super Administrador"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\NexoMarket Super Administrador"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir NexoMarket Super Administrador"; Flags: nowait postinstall skipifsilent
