@echo off
setlocal EnableExtensions
cd /d "%~dp0..\.."
call "%CD%\Herramientas\Admin\COMPILAR_FACIL_ADMIN.bat"
if errorlevel 1 exit /b 1
call "%CD%\Herramientas\Admin\CREAR_SETUP_ADMIN.bat"
exit /b %errorlevel%
