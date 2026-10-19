@echo off
rem Builds a self-contained Purgely (Sandbox has no .NET) and opens it in Windows Sandbox with a fake test program.
cd /d "%~dp0"
if not exist "%windir%\System32\WindowsSandbox.exe" (
  echo Windows Sandbox is not available on this PC.
  echo  - Windows 11 Pro/Enterprise/Education: run enable-sandbox.bat, then restart.
  echo  - Windows 11 Home has no Sandbox: use a VirtualBox virtual machine instead.
  pause & exit /b 1
)
echo Building a self-contained copy for the sandbox...
dotnet publish src\Purgely\Purgely.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist-sandbox -nologo || (pause & exit /b 1)
start "" "%~dp0tools\sandbox\Purgely-Sandbox.wsb"
