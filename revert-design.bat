@echo off
rem Puts back the window layouts from before the 0.2 design refresh (if the new look causes problems).
copy /Y "%~dp0_design_backup\*.xaml" "%~dp0src\Purgely\" >nul
echo Old window layouts restored. Run build.bat again.
pause
