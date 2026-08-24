@echo off
setlocal EnableExtensions
cd /d "%~dp0..\.."
if not exist "NexoMarket.Admin\SALIDA\NexoMarket.Admin.exe" (
  echo Primero ejecuta Herramientas\Admin\COMPILAR_FACIL_ADMIN.bat
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
if exist "Installer\Output\Admin" rmdir /s /q "Installer\Output\Admin"
mkdir "Installer\Output\Admin"
"%ISCC%" "Installer\NexoMarket_Admin_Setup.iss" /O"%CD%\Installer\Output\Admin" >"NexoMarket.Admin\logs_setup.log" 2>&1
if errorlevel 1 (
  type "NexoMarket.Admin\logs_setup.log"
  exit /b 1
)
echo SETUP ADMIN CREADO.
echo Installer\Output\Admin\NexoMarket_Setup.exe
exit /b 0
