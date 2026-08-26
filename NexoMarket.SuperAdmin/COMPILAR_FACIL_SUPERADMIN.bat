@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "MSBUILD="

for /f "delims=" %%A in ('where msbuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%A"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"

if not defined MSBUILD (
  echo ERROR: No se encontro MSBuild.
  echo Instala .NET Framework 4.8 Developer Pack o Visual Studio Build Tools.
  pause
  exit /b 1
)

if not exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll" if not exist "%ProgramFiles%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll" (
  echo ERROR: Faltan las Reference Assemblies de .NET Framework 4.8.
  echo Instala el .NET Framework 4.8 Developer Pack.
  pause
  exit /b 1
)

if exist "bin\Release" rmdir /s /q "bin\Release"
if exist "obj\Release" rmdir /s /q "obj\Release"

echo ================================================================
echo NEXOMARKET SUPER ADMIN 5.12.0
echo Compilando...
echo ================================================================
"%MSBUILD%" "NexoMarket.SuperAdmin.csproj" /t:Clean,Build /p:Configuration=Release /p:Platform=AnyCPU /v:minimal /nologo
if errorlevel 1 (
  echo.
  echo ERROR DE COMPILACION.
  pause
  exit /b 1
)

if not exist "bin\Release\NexoMarket.SuperAdmin.exe" (
  echo ERROR: no se genero NexoMarket.SuperAdmin.exe
  pause
  exit /b 1
)

echo.
echo ================================================================
echo SUPER ADMIN COMPILADO CORRECTAMENTE
echo EXE: %CD%\bin\Release\NexoMarket.SuperAdmin.exe
echo ================================================================
pause
exit /b 0
