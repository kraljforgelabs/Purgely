@echo off
rem UI test build: same Purgely, but runs WITHOUT administrator rights so Claude (computer use) can click it.
rem Without admin it cannot clean system areas - good for a safe UI review. Never publish this build.
cd /d "%~dp0"
:wait_closed
tasklist /FI "IMAGENAME eq Purgely.exe" 2>nul | find /I "Purgely.exe" >nul
if not errorlevel 1 (
  echo  Close Purgely first, then press a key.
  pause >nul
  goto wait_closed
)
dotnet publish src\Purgely\Purgely.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:UiTest=true -o dist-uitest -nologo
if errorlevel 1 (
  echo  BUILD FAILED - take a screenshot and send it to Claude.
  pause
  exit /b 1
)
start "" "%~dp0dist-uitest\Purgely.exe"
