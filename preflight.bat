@echo off
setlocal
cd /d "%~dp0"
set RESULT=OK
echo.
echo  ===== Purgely preflight =====
echo.
echo  [1/3] Release build...
dotnet build src\Purgely\Purgely.csproj -c Release -nologo -v quiet
if errorlevel 1 ( set RESULT=MUST FIX & echo  MUST FIX  Build failed. & goto :end )
echo  OK        Build
echo.
echo  [2/3] Unit tests...
dotnet test tests\Purgely.Tests\Purgely.Tests.csproj -c Release -nologo -v quiet
if errorlevel 1 ( set RESULT=MUST FIX & echo  MUST FIX  Unit tests failed. & goto :end )
echo  OK        Unit tests
echo.
echo  [3/3] Safety audit...
powershell -NoProfile -ExecutionPolicy Bypass -File tools\safety-audit.ps1
if errorlevel 2 ( set RESULT=CHECK ) else if errorlevel 1 ( set RESULT=MUST FIX )
:end
echo.
echo  ===== Preflight result: %RESULT% =====
echo.
pause
if "%RESULT%"=="OK" exit /b 0
if "%RESULT%"=="CHECK" exit /b 2
exit /b 1
