@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "ROOT=%CD%"
set "OUT=%ROOT%\bin\Release"
set "LOG=%ROOT%\SUPERADMIN_5_23_1_BUILD_LOG.txt"
set "ISS=%ROOT%\..\Installer\NexoMarket_SuperAdmin_Setup.iss"
set "SETUPDIR=%ROOT%\..\Installer\Output\SuperAdmin"
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
>"%LOG%" echo NexoMarket Super Admin 5.23.1 - BUILD LOG
>>"%LOG%" echo Fecha: %date% %time%
>>"%LOG%" echo Compilacion SIN MSBuild
cls
echo ================================================================
echo NEXOMARKET SUPER ADMIN 5.23.1
echo COMPILAR FACIL - SIN MSBUILD
echo ================================================================
echo.
if not exist "%CSC%" goto NO_CSC
if exist "%OUT%" rmdir /s /q "%OUT%" >>"%LOG%" 2>&1
mkdir "%OUT%" >>"%LOG%" 2>&1
echo [1/3] Compilando EXE...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"%OUT%\NexoMarket.SuperAdmin.exe" /win32icon:"%ROOT%\Assets\NexoMarket.ico" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "%ROOT%\Program.cs" "%ROOT%\MainForm.cs" "%ROOT%\ApiClient.cs" >>"%LOG%" 2>&1
if errorlevel 1 goto BUILD_ERROR
if not exist "%OUT%\NexoMarket.SuperAdmin.exe" goto EXE_ERROR
for %%F in ("%OUT%\NexoMarket.SuperAdmin.exe") do if %%~zF LSS 10240 goto EXE_ERROR
echo EXE creado correctamente.
echo.
echo [2/3] Buscando Inno Setup 6...
set "ISCC="
for /f "delims=" %%A in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%A"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if defined ISCC (
  if exist "%SETUPDIR%" rmdir /s /q "%SETUPDIR%" >>"%LOG%" 2>&1
  mkdir "%SETUPDIR%" >>"%LOG%" 2>&1
  echo [3/3] Creando Setup...
  "%ISCC%" "%ISS%" /O"%SETUPDIR%" >>"%LOG%" 2>&1
  if errorlevel 1 goto INNO_ERROR
  echo Setup creado.
  start "" explorer.exe "%SETUPDIR%"
) else (
  echo AVISO: Inno Setup 6 no encontrado. Se genero solo el EXE.
  >>"%LOG%" echo AVISO: Inno Setup 6 no encontrado. Se genero solo el EXE.
)
echo.
echo ================================================================
echo PROCESO TERMINADO
echo EXE: "%OUT%\NexoMarket.SuperAdmin.exe"
echo SETUP: "%SETUPDIR%"
echo LOG: "%LOG%"
echo ================================================================
start "" explorer.exe "%OUT%"
pause
exit /b 0
:NO_CSC
>>"%LOG%" echo ERROR: no se encontro csc.exe de .NET Framework.
echo ERROR: no se encontro csc.exe.
pause
exit /b 1
:BUILD_ERROR
>>"%LOG%" echo ERROR: fallo la compilacion.
echo ERROR DE COMPILACION. Revisar log.
pause
exit /b 1
:EXE_ERROR
>>"%LOG%" echo ERROR: csc termino pero no se encontro un EXE valido.
echo ERROR: no se genero un EXE valido.
pause
exit /b 1
:INNO_ERROR
>>"%LOG%" echo ERROR: Inno Setup fallo al crear el Setup.
echo ERROR: Inno Setup no pudo crear el Setup.
pause
exit /b 1
