@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-desktop-launcher.ps1"
if errorlevel 1 (
  echo.
  echo Setup failed. Please copy this screen and send it to ChatGPT.
  pause
  exit /b 1
)
echo.
echo Setup complete. You can now launch AI Multi Window from the desktop.
pause
