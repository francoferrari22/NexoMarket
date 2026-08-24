# NexoMarket 4.1.1

## Compilación Windows

### NexoMarket Admin
- `Herramientas/Admin/COMPILAR_FACIL_ADMIN.bat`
- `Herramientas/Admin/CREAR_SETUP_ADMIN.bat`
- `Herramientas/Admin/COMPILAR_Y_CREAR_SETUP_ADMIN.bat`

Salida: `NexoMarket.Admin/SALIDA/`

### NexoMarket License Manager
- `Herramientas/LicenseManager/COMPILAR_FACIL_LICENSE_MANAGER.bat`
- `Herramientas/LicenseManager/CREAR_SETUP_LICENSE_MANAGER.bat`
- `Herramientas/LicenseManager/COMPILAR_Y_CREAR_SETUP_LICENSE_MANAGER.bat`

Salida: `NexoMarket.LicenseManager/SALIDA/`

## Solución del error CS1529

`LicenseService.cs` contenía una segunda tanda de instrucciones `using` después de cerrar el namespace de `LicenseCore`. En C#, los `using` deben estar antes de las declaraciones de namespace/tipos del archivo. `LicenseCore` fue separado a `NexoMarket.Admin/LicenseCore.cs` y `LicenseService.cs` quedó únicamente con el servicio del administrador.

## Estructura

- `NexoMarket.Admin/` — aplicación Windows del vendedor/administrador.
- `NexoMarket.LicenseManager/` — generador y administrador de licencias.
- `NexoMarket.CentralServer/` — servidor central.
- `Herramientas/Admin/` — BAT exclusivos del Admin.
- `Herramientas/LicenseManager/` — BAT exclusivos del License Manager.
- `Installer/` — scripts de Inno Setup.
