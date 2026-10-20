@echo off
rem Publishes Purgely to https://github.com/kraljforgelabs/Purgely as "KraljForge Labs".
rem Sign-in is handled by Git Credential Manager (a browser window opens the first time) - no password is stored here.
setlocal
cd /d "%~dp0"
where git >nul 2>&1 || (echo Git is not installed. Install it with:  winget install --id Git.Git -e & pause & exit /b 1)

set REPO=https://github.com/kraljforgelabs/Purgely.git

if not exist .git git init -b main
rem Repository-local identity, the same one used for the other KraljForge Labs projects.
git config user.name "KraljForge Labs"
git config user.email "kraljforgelabs@gmail.com"

rem Safety: never publish private records or anything that looks like a secret.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\secret-scan.ps1" || (echo. & echo Publishing stopped - see above. & pause & exit /b 1)

rem Build leftovers that slipped in before .gitignore listed them.
git rm --cached -q --ignore-unmatch "src/Purgely/*_wpftmp.csproj" >nul 2>&1
git add -A
for /f "tokens=3 delims=<>" %%v in ('findstr /c:"<Version>" src\Purgely\Purgely.csproj') do set VER=%%v
set /p MSG=Short description of the changes (Enter = "Purgely %VER%"): 
if "%MSG%"=="" set MSG=Purgely %VER%
git diff --cached --quiet || git commit -m "%MSG%"

git remote remove origin >nul 2>&1
git remote add origin %REPO%

rem If the GitHub repository already has files (README/LICENSE made on github.com), merge them first; ours win on conflicts.
git fetch origin >nul 2>&1
git rev-parse --verify origin/main >nul 2>&1 && git merge origin/main --allow-unrelated-histories -X ours --no-edit

git push -u origin main || (echo. & echo Push failed - check that you are signed in to GitHub as kraljforgelabs. & pause & exit /b 1)
echo.
echo Done. Open https://github.com/kraljforgelabs/Purgely - tab "Actions" shows the build and tests.
pause
