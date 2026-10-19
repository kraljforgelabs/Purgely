@echo off
rem Real-PC test of Purgely with fake test programs. Your own files are never used as traps.
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
:menu
echo.
echo  ===== Purgely real-PC test =====
echo   1  Plant the test programs (restore point first)
echo   2  Check leftovers and traps
echo   3  Check ads ^& suggestions (run before, after Turn off, after Undo)
echo   4  Remove all test items (when finished)
echo   0  Exit
set /p choice= Choose:
if "%choice%"=="1" powershell -NoProfile -ExecutionPolicy Bypass -File tools\realpc\plant.ps1
if "%choice%"=="2" powershell -NoProfile -ExecutionPolicy Bypass -File tools\realpc\check.ps1
if "%choice%"=="3" powershell -NoProfile -ExecutionPolicy Bypass -File tools\realpc\check-ads.ps1
if "%choice%"=="4" powershell -NoProfile -ExecutionPolicy Bypass -File tools\realpc\remove-test.ps1
if "%choice%"=="0" exit /b
goto menu
