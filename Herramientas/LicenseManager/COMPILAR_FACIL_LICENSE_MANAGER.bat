@echo off
setlocal EnableExtensions
cd /d "%~dp0..\.."
set "LOG=%CD%\NexoMarket.LicenseManager\logs_compilar_facil.log"
set "MSBUILD="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if not defined MSBUILD for /f "delims=" %%A in ('where msbuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%A"
if not defined MSBUILD (
  echo ERROR: No se encontro MSBuild de .NET Framework 4.8.
  exit /b 1
)
if exist "NexoMarket.LicenseManager\bin\Release" rmdir /s /q "NexoMarket.LicenseManager\bin\Release"
if exist "NexoMarket.LicenseManager\obj\Release" rmdir /s /q "NexoMarket.LicenseManager\obj\Release"
if exist "NexoMarket.LicenseManager\SALIDA" rmdir /s /q "NexoMarket.LicenseManager\SALIDA"
mkdir "NexoMarket.LicenseManager\SALIDA"
"%MSBUILD%" "NexoMarket.LicenseManager\NexoMarket.LicenseManager.csproj" /t:Rebuild /p:Configuration=Release /p:Platform="AnyCPU" /v:minimal /nologo >"%LOG%" 2>&1
if errorlevel 1 (
  echo ERROR DE COMPILACION DEL LICENSE MANAGER.
  type "%LOG%"
  exit /b 1
)
if not exist "NexoMarket.LicenseManager\bin\Release\NexoMarket License Manager.exe" (
  echo ERROR: No se genero el License Manager.
  exit /b 1
)
xcopy /E /I /Y "NexoMarket.LicenseManager\bin\Release\*" "NexoMarket.LicenseManager\SALIDA\" >>"%LOG%" 2>&1
if errorlevel 1 exit /b 1
echo LICENSE MANAGER COMPILADO CORRECTAMENTE.
echo SALIDA: NexoMarket.LicenseManager\SALIDA\NexoMarket License Manager.exe
exit /b 0
