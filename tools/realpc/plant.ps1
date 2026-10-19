# Plants TEST programs on this PC for a real-PC Purgely test. Nothing of yours is touched:
# every trap points at a dummy folder made by this script, and every created item is listed in a manifest
# so remove-test.ps1 can take exactly those away again.
$ErrorActionPreference = 'Stop'
$results = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'sandbox-results'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$manifest = Join-Path $results 'realpc-created.txt'
if (Test-Path $manifest) { Write-Host 'Test items already planted. Run remove-test first, or go on with the test.' -ForegroundColor Yellow; exit 0 }
$created = New-Object System.Collections.Generic.List[string]
function Track($item) { $created.Add($item); $created | Set-Content $manifest -Encoding UTF8 }

# Safety net first: a restore point (Windows allows one per 24 h - if one exists already, that one counts).
try { Checkpoint-Computer -Description 'Before Purgely real-PC test' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; Write-Host 'Restore point created.' -ForegroundColor Green }
catch { Write-Host "Restore point: $($_.Exception.Message)" -ForegroundColor Yellow }

$pf = $env:ProgramFiles
$vendor = Join-Path $pf 'PurgelyTest Contoso'
foreach ($p in @($vendor, "$env:APPDATA\Contoso PhotoTool", "$env:LOCALAPPDATA\PhotoTool", "$env:USERPROFILE\PurgelyTest Trap")) {
  if (Test-Path $p) { throw "Stopped: $p already exists - not touching anything." }
}
if (Test-Path 'HKCU:\Software\Contoso') { throw 'Stopped: HKCU\Software\Contoso already exists - not touching anything.' }
if (Get-ScheduledTask -TaskPath '\Contoso\' -TaskName 'PhotoTool Update' -ErrorAction SilentlyContinue) { throw 'Stopped: the test task already exists.' }
$app = Join-Path $vendor 'PhotoTool'; $backup = Join-Path $vendor 'Backup'
New-Item -ItemType Directory -Force -Path $app, (Join-Path $app 'plugins'), $backup | Out-Null; Track "DIR|$vendor"
Set-Content -Value "fake test program - not a real exe" -Path (Join-Path $app 'PhotoTool.exe')
Set-Content -Value "fake test program - not a real exe" -Path (Join-Path $app 'PhotoToolUpdater.exe')
Set-Content (Join-Path $app 'plugins\filters.dat') ('x' * 5000)
Set-Content -Value "fake test program - not a real exe" -Path (Join-Path $backup 'ContosoBackup.exe')

New-Item -ItemType Directory -Force -Path "$env:APPDATA\Contoso PhotoTool" | Out-Null; Track "DIR|$env:APPDATA\Contoso PhotoTool"
New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\PhotoTool\cache" | Out-Null; Track "DIR|$env:LOCALAPPDATA\PhotoTool"
Set-Content "$env:APPDATA\Contoso PhotoTool\settings.json" '{"theme":"dark"}'
Set-Content "$env:LOCALAPPDATA\PhotoTool\cache\thumbs.db" ('y' * 20000)

# TRAP folder (stands in for "your Documents"): a junction inside the leftover folder points at it.
$trap = "$env:USERPROFILE\PurgelyTest Trap"
New-Item -ItemType Directory -Force -Path "$trap\PhotoTool Projects" | Out-Null; Track "DIR|$trap"
Set-Content "$trap\PhotoTool Projects\holiday.txt" 'pretend this is your own work - must survive'
Set-Content "$trap\important.txt" 'must survive'
cmd /c mklink /J "$app\docs-link" "$trap" | Out-Null

New-Item -Force 'HKCU:\Software\Contoso\PhotoTool' | Out-Null; Track 'KEY|HKCU:\Software\Contoso'
Set-ItemProperty 'HKCU:\Software\Contoso\PhotoTool' -Name LastFolder -Value 'C:\Pictures'
New-Item -Force 'HKCU:\Software\Contoso\Backup' | Out-Null
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PhotoToolUpdater' -Value "`"$app\PhotoToolUpdater.exe`" /background"
Track 'RUN|PhotoToolUpdater'

$u = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PurgelyTestPhotoTool'
New-Item -Force $u | Out-Null; Track "KEY|$u"
Set-ItemProperty $u -Name DisplayName -Value 'Contoso PhotoTool'
Set-ItemProperty $u -Name Publisher -Value 'Contoso Ltd.'
Set-ItemProperty $u -Name DisplayVersion -Value '3.2.1'
Set-ItemProperty $u -Name InstallLocation -Value $app
Set-ItemProperty $u -Name UninstallString -Value "`"$app\uninstall.exe`""
$b = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PurgelyTestBackup'
New-Item -Force $b | Out-Null; Track "KEY|$b"
Set-ItemProperty $b -Name DisplayName -Value 'Contoso Backup'
Set-ItemProperty $b -Name Publisher -Value 'Contoso Ltd.'
Set-ItemProperty $b -Name InstallLocation -Value $backup
Set-ItemProperty $b -Name UninstallString -Value "`"$backup\ContosoBackup.exe`" /uninstall"

Register-ScheduledTask -TaskPath '\Contoso\' -TaskName 'PhotoTool Update' -Action (New-ScheduledTaskAction -Execute "$app\PhotoToolUpdater.exe") -Trigger (New-ScheduledTaskTrigger -Daily -At 9am) -Force | Out-Null
Track 'TASK|\Contoso\PhotoTool Update'
$lnkDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Contoso PhotoTool'
New-Item -ItemType Directory -Force -Path $lnkDir | Out-Null; Track "DIR|$lnkDir"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $lnkDir 'PhotoTool.lnk'))
$s.TargetPath = Join-Path $app 'PhotoTool.exe'; $s.Save()

# Known-program rule test with Dropbox - only when Dropbox is NOT on this PC.
$dropboxInstalled = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
  Where-Object { $_.DisplayName -like 'Dropbox*' }
if ($dropboxInstalled -or (Test-Path "$env:LOCALAPPDATA\Dropbox") -or (Test-Path "$env:USERPROFILE\Dropbox")) {
  Write-Host 'Dropbox is on this PC - the known-program rule test is skipped (your Dropbox is not touched).' -ForegroundColor Yellow
} else {
  New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\Dropbox\Update", "$env:LOCALAPPDATA\Dropbox\instance1" | Out-Null; Track "DIR|$env:LOCALAPPDATA\Dropbox"
  Set-Content "$env:LOCALAPPDATA\Dropbox\Update\update.log" ('u' * 3000)
  Set-Content "$env:LOCALAPPDATA\Dropbox\instance1\config.db" 'pretend settings - offered but must NOT be ticked'
  New-Item -ItemType Directory -Force -Path "$env:USERPROFILE\Dropbox" | Out-Null; Track "DIR|$env:USERPROFILE\Dropbox"
  Set-Content "$env:USERPROFILE\Dropbox\my-file.txt" 'pretend this is your Dropbox file - must survive'
  $d = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PurgelyTestDropbox'
  New-Item -Force $d | Out-Null; Track "KEY|$d"
  Set-ItemProperty $d -Name DisplayName -Value 'Dropbox'
  Set-ItemProperty $d -Name Publisher -Value 'Dropbox, Inc.'
  Set-ItemProperty $d -Name UninstallString -Value "`"$env:LOCALAPPDATA\Dropbox\missing\DropboxUninstaller.exe`""
}
Write-Host ''
Write-Host 'Test programs planted: "Contoso PhotoTool" (removed), "Contoso Backup" (installed - trap), "Dropbox" (removed, if planted).' -ForegroundColor Green
