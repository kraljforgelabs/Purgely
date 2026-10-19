# Checks the real-PC test: leftovers gone after cleaning (back after Undo), traps ALWAYS there.
$results = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'sandbox-results'
$app = "$env:ProgramFiles\PurgelyTest Contoso\PhotoTool"
$trap = "$env:USERPROFILE\PurgelyTest Trap"
$dropbox = Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PurgelyTestDropbox'
$leftovers = [ordered]@{
  'PhotoTool install folder' = { Test-Path $app }
  'PhotoTool AppData'        = { Test-Path "$env:APPDATA\Contoso PhotoTool" }
  'PhotoTool local cache'    = { Test-Path "$env:LOCALAPPDATA\PhotoTool" }
  'PhotoTool registry key'   = { Test-Path 'HKCU:\Software\Contoso\PhotoTool' }
  'PhotoTool Run value'      = { $null -ne (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PhotoToolUpdater -ErrorAction SilentlyContinue) }
  'PhotoTool uninstall entry'= { Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PurgelyTestPhotoTool' }
  'PhotoTool scheduled task' = { (schtasks /Query /TN '\Contoso\PhotoTool Update' 2>$null) -ne $null }
  'PhotoTool Start shortcut' = { Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Contoso PhotoTool" }
}
if ($dropbox -or (Test-Path "$env:LOCALAPPDATA\Dropbox\Update")) {
  $leftovers['Dropbox update folder (rule)'] = { Test-Path "$env:LOCALAPPDATA\Dropbox\Update" }
}
$traps = [ordered]@{
  'Trap folder file (junction target)'   = { Test-Path "$trap\important.txt" }
  'Trap "PhotoTool Projects" file'       = { Test-Path "$trap\PhotoTool Projects\holiday.txt" }
  'Contoso Backup program'               = { Test-Path "$env:ProgramFiles\PurgelyTest Contoso\Backup\ContosoBackup.exe" }
  'Contoso Backup registry key'          = { Test-Path 'HKCU:\Software\Contoso\Backup' }
  'Contoso Backup uninstall entry'       = { Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PurgelyTestBackup' }
  'Your Documents folder'                = { Test-Path ([Environment]::GetFolderPath('MyDocuments')) }
  'Your Desktop folder'                  = { Test-Path ([Environment]::GetFolderPath('DesktopDirectory')) }
}
if ((Test-Path "$env:USERPROFILE\Dropbox\my-file.txt") -or $dropbox) {
  $traps['Dropbox user file'] = { Test-Path "$env:USERPROFILE\Dropbox\my-file.txt" }
}
$lines = @("Purgely real-PC check - $(Get-Date -Format 'yyyy-MM-dd HH:mm')", '', 'Leftovers (GONE after cleaning, BACK after Undo):')
foreach ($k in $leftovers.Keys) { $lines += ('  {0,-30} {1}' -f $k, $(if (& $leftovers[$k]) { 'still there' } else { 'gone' })) }
$lines += ''; $lines += 'Dropbox settings (offered, must NOT be ticked - normally still there):'
$lines += ('  {0,-30} {1}' -f 'Dropbox instance1 settings', $(if (Test-Path "$env:LOCALAPPDATA\Dropbox\instance1") { 'still there' } else { 'gone (was it ticked by you?)' }))
$lines += ''; $lines += 'Traps (must ALWAYS be there):'
$bad = 0
foreach ($k in $traps.Keys) { $ok = & $traps[$k]; if (-not $ok) { $bad++ }; $lines += ('  {0,-40} {1}' -f $k, $(if ($ok) { 'OK' } else { 'MISSING  <-- MUST FIX' })) }
$lines += ''; $lines += $(if ($bad -eq 0) { 'RESULT: OK - no trap was touched.' } else { "RESULT: MUST FIX - $bad trap(s) removed!" })
$lines | ForEach-Object { Write-Host $_ }
$file = Join-Path $results "realpc-check-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
$lines | Set-Content $file -Encoding UTF8
Write-Host "Saved: $file" -ForegroundColor Cyan
