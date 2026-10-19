@echo off
rem Makes a downloadable release: tags the current version and pushes the tag.
rem GitHub Actions (.github/workflows/release.yml) then runs the tests and the safety audit, builds Purgely.exe
rem and attaches it to a GitHub Release (tab "Releases" on the right side of the repository page).
setlocal
cd /d "%~dp0"
for /f "tokens=3 delims=<>" %%v in ('findstr /c:"<Version>" src\Purgely\Purgely.csproj') do set VER=%%v
if "%VER%"=="" (echo Could not read the version from Purgely.csproj. & pause & exit /b 1)

git diff --quiet && git diff --cached --quiet || (echo There are changes that are not on GitHub yet - run publish-to-github.bat first. & pause & exit /b 1)
git fetch origin >nul 2>&1
for /f %%a in ('git rev-parse HEAD') do set LOCAL=%%a
for /f %%a in ('git rev-parse origin/main') do set REMOTE=%%a
if not "%LOCAL%"=="%REMOTE%" (echo GitHub is not up to date - run publish-to-github.bat first. & pause & exit /b 1)

git rev-parse -q --verify "refs/tags/v%VER%" >nul && (echo Version %VER% was already released. Raise ^<Version^> in src\Purgely\Purgely.csproj first. & pause & exit /b 1)

echo Releasing Purgely %VER% ...
git tag -a v%VER% -m "Purgely %VER%" || (pause & exit /b 1)
git push origin v%VER% || (echo Push failed. & git tag -d v%VER% & pause & exit /b 1)
echo.
echo Done. GitHub is now building the release (about 10 minutes).
echo Watch it under "Actions"; when it is green, Purgely.exe is under "Releases":
echo   https://github.com/kraljforgelabs/Purgely/releases
start "" https://github.com/kraljforgelabs/Purgely/actions
pause
