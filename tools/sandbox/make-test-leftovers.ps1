# Creates "Contoso PhotoTool" as a REMOVED program that left traces behind, plus things that must NEVER be removed.
$ErrorActionPreference = 'Continue'
$pf = $env:ProgramFiles
$vendor = Join-Path $pf 'Contoso'
$app = Join-Path $vendor 'PhotoTool'
$backup = Join-Path $vendor 'Backup'          # TRAP: another Contoso program that is still installed
New-Item -ItemType Directory -Force -Path $app, (Join-Path $app 'plugins'), (Join-Path $app 'empty'), $backup | Out-Null
Set-Content -Value "fake test program - not a real exe" -Path (Join-Path $app 'PhotoTool.exe')
Set-Content -Value "fake test program - not a real exe" -Path (Join-Path $app 'PhotoToolUpdater.exe')
Set-Content (Join-Path $app 'plugins\filters.dat') ('x' * 5000)
Set-Content -Value "fake test program - not a real exe" -Path (Join-Path $backup 'ContosoBackup.exe')

# Leftover data
New-Item -ItemType Directory -Force -Path "$env:APPDATA\Contoso PhotoTool", "$env:LOCALAPPDATA\PhotoTool\cache" | Out-Null
Set-Content "$env:APPDATA\Contoso PhotoTool\settings.json" '{"theme":"dark"}'
Set-Content "$env:LOCALAPPDATA\PhotoTool\cache\thumbs.db" ('y' * 20000)

# TRAP: user's own files whose folder name contains the program name
New-Item -ItemType Directory -Force -Path "$env:USERPROFILE\Documents\PhotoTool Projects" | Out-Null
Set-Content "$env:USERPROFILE\Documents\PhotoTool Projects\holiday.txt" 'my own work - must survive'

# TRAP: a junction inside the leftover folder pointing at the user's Documents
cmd /c mklink /J "$app\docs-link" "$env:USERPROFILE\Documents" | Out-Null

# Registry leftovers
New-Item -Force 'HKCU:\Software\Contoso\PhotoTool' | Out-Null
Set-ItemProperty 'HKCU:\Software\Contoso\PhotoTool' -Name LastFolder -Value 'C:\Pictures'
New-Item -Force 'HKCU:\Software\Contoso\Backup' | Out-Null        # TRAP: belongs to Contoso Backup
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PhotoToolUpdater' -Value "`"$app\PhotoToolUpdater.exe`" /background"

# Removed program: uninstall entry whose uninstaller no longer exists -> shows as "removed, left traces"
$u = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoPhotoTool'
New-Item -Force $u | Out-Null
Set-ItemProperty $u -Name DisplayName -Value 'Contoso PhotoTool'
Set-ItemProperty $u -Name Publisher -Value 'Contoso Ltd.'
Set-ItemProperty $u -Name DisplayVersion -Value '3.2.1'
Set-ItemProperty $u -Name InstallLocation -Value $app
Set-ItemProperty $u -Name UninstallString -Value "`"$app\uninstall.exe`""

# Still installed program (TRAP)
$b = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoBackup'
New-Item -Force $b | Out-Null
Set-ItemProperty $b -Name DisplayName -Value 'Contoso Backup'
Set-ItemProperty $b -Name Publisher -Value 'Contoso Ltd.'
Set-ItemProperty $b -Name InstallLocation -Value $backup
Set-ItemProperty $b -Name UninstallString -Value "`"$backup\ContosoBackup.exe`" /uninstall"

# Scheduled task and Start menu shortcut
schtasks /Create /TN '\Contoso\PhotoTool Update' /TR "`"$app\PhotoToolUpdater.exe`"" /SC DAILY /ST 09:00 /F | Out-Null
$lnkDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Contoso PhotoTool'
New-Item -ItemType Directory -Force -Path $lnkDir | Out-Null
$s = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $lnkDir 'PhotoTool.lnk'))
$s.TargetPath = Join-Path $app 'PhotoTool.exe'; $s.Save()

# 0.3: known-program rule - a removed "Microsoft OneDrive" with leftovers in a place the name search can't find,
# and the user's OneDrive files folder, which must NEVER be touched.
New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\Microsoft\OneDrive\logs", "$env:USERPROFILE\OneDrive\Documents" | Out-Null
Set-Content "$env:LOCALAPPDATA\Microsoft\OneDrive\logs\sync.log" ('z' * 3000)
Set-Content "$env:USERPROFILE\OneDrive\Documents\thesis.txt" 'my own OneDrive file - must survive'
New-Item -Force 'HKCU:\Software\Microsoft\OneDrive' | Out-Null
Set-ItemProperty 'HKCU:\Software\Microsoft\OneDrive' -Name UserFolder -Value "$env:USERPROFILE\OneDrive"
$o = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe'
New-Item -Force $o | Out-Null
Set-ItemProperty $o -Name DisplayName -Value 'Microsoft OneDrive'
Set-ItemProperty $o -Name Publisher -Value 'Microsoft Corporation'
Set-ItemProperty $o -Name UninstallString -Value "`"$env:LOCALAPPDATA\Microsoft\OneDrive\missing\OneDriveSetup.exe`" /uninstall"

Write-Host 'Test programs "Contoso PhotoTool" and "Microsoft OneDrive" planted (with traps).' -ForegroundColor Green
