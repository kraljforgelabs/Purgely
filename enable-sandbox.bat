@echo off
rem Turns on Windows Sandbox (Windows 11 Pro/Enterprise/Education). Needs administrator rights and a restart.
net session >nul 2>&1
if errorlevel 1 (
  echo Asking for administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Turning on Windows Sandbox...
dism /online /enable-feature /featurename:Containers-DisposableClientVM /all /norestart
if errorlevel 3010 goto restart
if errorlevel 1 goto failed
:restart
echo.
echo Done. Restart the PC, then run sandbox.bat again.
pause
exit /b 0
:failed
echo.
echo Could not turn it on. If it mentions virtualization, turn on "Intel Virtualization Technology" in the BIOS.
echo Take a screenshot of this window and send it to Claude.
pause
exit /b 1
