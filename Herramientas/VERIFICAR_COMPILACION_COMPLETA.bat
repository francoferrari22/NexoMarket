@echo off
setlocal EnableExtensions
cd /d "%~dp0.."
set "ROOT=%CD%"
set "LOG=%ROOT%\VERIFICACION_COMPILACION_COMPLETA.log"
>"%LOG%" echo ================================================================
>>"%LOG%" echo NexoMarket 4.2.1 - VERIFICACION COMPLETA
>>"%LOG%" echo Fecha: %DATE% %TIME%
>>"%LOG%" echo ================================================================

set "MSBUILD="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if not defined MSBUILD for /f "delims=" %%A in ('where msbuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%A"
if not defined MSBUILD (
  echo ERROR: No se encontro MSBuild.
  exit /b 1
)

echo [1/2] Compilando NexoMarket Admin...
>>"%LOG%" echo [ADMIN]
"%MSBUILD%" "%ROOT%\NexoMarket.Admin\NexoMarket.Admin.csproj" /t:Rebuild /p:Configuration=Release /p:Platform="AnyCPU" /v:minimal /nologo >>"%LOG%" 2>&1
if errorlevel 1 (
  echo ERROR: ADMIN NO COMPILA. Ver: "%LOG%"
  type "%LOG%"
  exit /b 1
)

echo [2/2] Compilando NexoMarket License Manager...
>>"%LOG%" echo [LICENSE MANAGER]
"%MSBUILD%" "%ROOT%\NexoMarket.LicenseManager\NexoMarket.LicenseManager.csproj" /t:Rebuild /p:Configuration=Release /p:Platform="AnyCPU" /v:minimal /nologo >>"%LOG%" 2>&1
if errorlevel 1 (
  echo ERROR: LICENSE MANAGER NO COMPILA. Ver: "%LOG%"
  type "%LOG%"
  exit /b 1
)

where dotnet.exe >nul 2>&1
if errorlevel 1 (
  echo AVISO: dotnet no esta instalado; se omite CentralServer local.
  >>"%LOG%" echo [CENTRAL SERVER] OMITIDO: dotnet no disponible.
) else (
  echo [EXTRA] Compilando CentralServer con dotnet...
  >>"%LOG%" echo [CENTRAL SERVER]
  dotnet build "%ROOT%\NexoMarket.CentralServer\NexoMarket.CentralServer.csproj" -c Release --nologo >>"%LOG%" 2>&1
  if errorlevel 1 (
    echo ERROR: CENTRAL SERVER NO COMPILA. Ver: "%LOG%"
    type "%LOG%"
    exit /b 1
  )
)

echo.
echo ================================================================
echo VERIFICACION COMPLETA CORRECTA.
echo Log: "%LOG%"
echo ================================================================
exit /b 0
