@echo off
setlocal
set ROOT=%~dp0
set MSBUILD=
for %%P in (
 "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
 "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
 "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
 "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
 "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
 "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
) do if not defined MSBUILD if exist %%~P set MSBUILD=%%~P
if not defined MSBUILD (
 echo No se encontro MSBuild.
 echo Instala Visual Studio/Build Tools con Desktop development with .NET Framework.
 pause
 exit /b 1
)
echo Compilando NexoMarket Super Admin...
"%MSBUILD%" "%ROOT%NexoMarket.SuperAdmin.csproj" /t:Clean,Build /p:Configuration=Release /p:Platform=AnyCPU /m
if errorlevel 1 (
 echo ERROR DE COMPILACION.
 pause
 exit /b 1
)
echo.
echo LISTO: %ROOT%bin\Release\NexoMarket.SuperAdmin.exe
pause
