@echo off
setlocal EnableExtensions
cd /d "%~dp0..\.."
set "LOG=%CD%\NexoMarket.Admin\logs_compilar_facil.log"
set "MSBUILD="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if not defined MSBUILD for /f "delims=" %%A in ('where msbuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%A"
if not defined MSBUILD (
  echo ERROR: No se encontro MSBuild de .NET Framework 4.8.
  exit /b 1
)
if exist "NexoMarket.Admin\bin\Release" rmdir /s /q "NexoMarket.Admin\bin\Release"
if exist "NexoMarket.Admin\obj\Release" rmdir /s /q "NexoMarket.Admin\obj\Release"
if exist "NexoMarket.Admin\SALIDA" rmdir /s /q "NexoMarket.Admin\SALIDA"
mkdir "NexoMarket.Admin\SALIDA"
"%MSBUILD%" "NexoMarket.Admin\NexoMarket.Admin.csproj" /t:Rebuild /p:Configuration=Release /p:Platform="AnyCPU" /v:minimal /nologo >"%LOG%" 2>&1
if errorlevel 1 (
  echo ERROR DE COMPILACION DEL ADMIN.
  type "%LOG%"
  exit /b 1
)
if not exist "NexoMarket.Admin\bin\Release\NexoMarket.Admin.exe" (
  echo ERROR: No se genero NexoMarket.Admin.exe.
  exit /b 1
)
xcopy /E /I /Y "NexoMarket.Admin\bin\Release\*" "NexoMarket.Admin\SALIDA\" >>"%LOG%" 2>&1
if errorlevel 1 exit /b 1
echo ADMIN NEXOMARKET COMPILADO CORRECTAMENTE.
echo SALIDA: NexoMarket.Admin\SALIDA\NexoMarket.Admin.exe
exit /b 0
