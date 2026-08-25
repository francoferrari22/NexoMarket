@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0..\.."
set "LOG=%CD%\NexoMarket.Admin\logs_compilar_facil.log"
>"%LOG%" echo ================================================================
>>"%LOG%" echo NexoMarket Admin - diagnostico de compilacion
>>"%LOG%" echo Fecha: %date% %time%
>>"%LOG%" echo ================================================================
set "MSBUILD="
for /f "delims=" %%A in ('where msbuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%A"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if not defined MSBUILD (
  echo ERROR: No se encontro MSBuild. Consulta el log: "%LOG%"
  >>"%LOG%" echo ERROR: No se encontro MSBuild.
  goto :FAIL
)
>>"%LOG%" echo MSBuild: %MSBUILD%
set "REF48="
if exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll" set "REF48=1"
if exist "%ProgramFiles%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll" set "REF48=1"
if not defined REF48 (
  echo ERROR: Falta .NET Framework 4.8 Developer Pack / Reference Assemblies.
  >>"%LOG%" echo ERROR MSB3644: No se encontraron los ensamblados de referencia de .NETFramework v4.8.
  >>"%LOG%" echo Instalar .NET Framework 4.8 Developer Pack y volver a ejecutar.
  goto :FAIL
)
if exist "NexoMarket.Admin\bin\Release" rmdir /s /q "NexoMarket.Admin\bin\Release"
if exist "NexoMarket.Admin\obj\Release" rmdir /s /q "NexoMarket.Admin\obj\Release"
if exist "NexoMarket.Admin\SALIDA" rmdir /s /q "NexoMarket.Admin\SALIDA"
mkdir "NexoMarket.Admin\SALIDA"
"%MSBUILD%" "NexoMarket.Admin\NexoMarket.Admin.csproj" /t:Clean,Build /p:Configuration=Release /p:Platform="AnyCPU" /v:minimal /nologo >>"%LOG%" 2>&1
if errorlevel 1 (
  echo ERROR DE COMPILACION. El log completo queda en:
  echo %LOG%
  goto :FAIL
)
if not exist "NexoMarket.Admin\bin\Release\NexoMarket.Admin.exe" (
  >>"%LOG%" echo ERROR: no se genero el EXE.
  echo ERROR: no se genero NexoMarket.Admin.exe. Revisa el log.
  goto :FAIL
)
xcopy /E /I /Y "NexoMarket.Admin\bin\Release\*" "NexoMarket.Admin\SALIDA\" >>"%LOG%" 2>&1
if errorlevel 1 goto :FAIL
echo.
echo ================================================================
echo ADMIN NEXOMARKET COMPILADO CORRECTAMENTE
echo EXE: %CD%\NexoMarket.Admin\SALIDA\NexoMarket.Admin.exe
echo LOG: %LOG%
echo ================================================================
pause
exit /b 0
:FAIL
echo.
echo ================================================================
echo COMPILACION FALLIDA - LA VENTANA SE MANTIENE ABIERTA PARA VER EL ERROR
echo LOG: %LOG%
echo ================================================================
pause
exit /b 1
