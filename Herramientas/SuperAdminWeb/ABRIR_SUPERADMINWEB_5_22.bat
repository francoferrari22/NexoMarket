@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "LOG=%~dp0SUPERADMINWEB_5_22_LOG.txt"
>"%LOG%" echo NexoMarket Super Admin Web 5.22
>>"%LOG%" echo Fecha: %date% %time%
if not exist "%~dp0NexoMarket_SuperAdmin_5_22.hta" (
  echo ERROR: no se encontro el HTA.
  >>"%LOG%" echo ERROR: no se encontro el HTA.
  pause
  exit /b 1
)
where mshta.exe >nul 2>&1
if errorlevel 1 (
  echo ERROR: mshta.exe no esta disponible.
  >>"%LOG%" echo ERROR: mshta.exe no esta disponible.
  pause
  exit /b 1
)
start "" mshta.exe "%~dp0NexoMarket_SuperAdmin_5_22.hta"
exit /b 0
