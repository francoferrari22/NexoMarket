@echo off
setlocal EnableExtensions
cd /d "%~dp0..\.."
if not exist "NexoMarket.LicenseManager\SALIDA\NexoMarket License Manager.exe" (
  echo Primero ejecuta Herramientas\LicenseManager\COMPILAR_FACIL_LICENSE_MANAGER.bat
  exit /b 1
)
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC for /f "delims=" %%A in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%A"
if not defined ISCC (
  echo ERROR: No se encontro Inno Setup 6.
  exit /b 1
)
if exist "Installer\Output\LicenseManager" rmdir /s /q "Installer\Output\LicenseManager"
mkdir "Installer\Output\LicenseManager"
"%ISCC%" "Installer\NexoMarket_LicenseManager_Setup.iss" /O"%CD%\Installer\Output\LicenseManager" >"NexoMarket.LicenseManager\logs_setup.log" 2>&1
if errorlevel 1 (
  type "NexoMarket.LicenseManager\logs_setup.log"
  exit /b 1
)
echo SETUP LICENSE MANAGER CREADO.
echo Installer\Output\LicenseManager\NexoMarket_LicenseManager_Setup.exe
exit /b 0
