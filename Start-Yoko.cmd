@echo off
setlocal
cd /d "%~dp0"

where pwsh.exe >nul 2>nul
if errorlevel 1 goto windows_powershell

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Yoko.ps1" %*
goto finished

:windows_powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Yoko.ps1" %*

:finished
set "YOKO_EXIT=%ERRORLEVEL%"
if "%YOKO_EXIT%"=="0" exit /b 0

echo.
echo Yoko's launcher stopped with error code %YOKO_EXIT%.
echo Review the message above before closing this window.
pause
exit /b %YOKO_EXIT%
