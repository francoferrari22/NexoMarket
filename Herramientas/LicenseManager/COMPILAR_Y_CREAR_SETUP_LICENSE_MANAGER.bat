@echo off
setlocal EnableExtensions
cd /d "%~dp0..\.."
call "%CD%\Herramientas\LicenseManager\COMPILAR_FACIL_LICENSE_MANAGER.bat"
if errorlevel 1 exit /b 1
call "%CD%\Herramientas\LicenseManager\CREAR_SETUP_LICENSE_MANAGER.bat"
exit /b %errorlevel%
