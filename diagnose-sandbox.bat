@echo off
rem Read-only check of why Windows Sandbox disconnects. Saves sandbox-results\sandbox-diagnosis.txt for Claude.
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\sandbox\diagnose-sandbox.ps1"
echo.
echo Done - tell Claude.
pause
