@echo off
cd /d "%~dp0"
:wait_closed
tasklist /FI "IMAGENAME eq Purgely.exe" 2>nul | find /I "Purgely.exe" >nul
if not errorlevel 1 (
  echo  Purgely is still open - the new version cannot replace it.
  echo  Close Purgely ^(wait until any cleanup has finished^), then press a key.
  pause >nul
  goto wait_closed
)
echo.
echo  Building Purgely...
echo.
dotnet publish src\Purgely\Purgely.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if errorlevel 1 (
  echo.
  echo  BUILD FAILED - take a screenshot of this window and send it to Claude.
  pause
  exit /b 1
)
echo.
echo  Build succeeded. Starting Purgely...
start "" "%~dp0dist\Purgely.exe"
