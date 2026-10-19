# Checks the result of a cleanup: leftovers should be gone, traps must still be there. Saves a report.
$leftovers = [ordered]@{
  'Install folder'        = { Test-Path "$env:ProgramFiles\Contoso\PhotoTool" }
  'AppData settings'      = { Test-Path "$env:APPDATA\Contoso PhotoTool" }
  'Local cache'           = { Test-Path "$env:LOCALAPPDATA\PhotoTool" }
  'Registry key'          = { Test-Path 'HKCU:\Software\Contoso\PhotoTool' }
  'Run value'             = { $null -ne (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PhotoToolUpdater -ErrorAction SilentlyContinue) }
  'Uninstall entry'       = { Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoPhotoTool' }
  'Scheduled task'        = { (schtasks /Query /TN '\Contoso\PhotoTool Update' 2>$null) -ne $null }
  'Start menu shortcut'   = { Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Contoso PhotoTool" }
  'OneDrive app data'     = { Test-Path "$env:LOCALAPPDATA\Microsoft\OneDrive" }
  'OneDrive registry key' = { Test-Path 'HKCU:\Software\Microsoft\OneDrive' }
}
$traps = [ordered]@{
  'Documents\PhotoTool Projects\holiday.txt' = { Test-Path "$env:USERPROFILE\Documents\PhotoTool Projects\holiday.txt" }
  'Documents folder (junction target)'       = { (Get-ChildItem "$env:USERPROFILE\Documents" -Force | Measure-Object).Count -gt 0 }
  'Contoso Backup program folder'            = { Test-Path "$env:ProgramFiles\Contoso\Backup\ContosoBackup.exe" }
  'Contoso Backup registry key'              = { Test-Path 'HKCU:\Software\Contoso\Backup' }
  'Contoso Backup uninstall entry'           = { Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoBackup' }
  'OneDrive\Documents\thesis.txt (user files)' = { Test-Path "$env:USERPROFILE\OneDrive\Documents\thesis.txt" }
  'Microsoft key (parent of OneDrive key)'   = { Test-Path 'HKCU:\Software\Microsoft\Windows' }
}
$lines = @("Purgely sandbox check - $(Get-Date -Format 'yyyy-MM-dd HH:mm')", '', 'Leftovers (should be GONE after cleaning, BACK after Undo):')
foreach ($k in $leftovers.Keys) { $lines += ('  {0,-28} {1}' -f $k, $(if (& $leftovers[$k]) { 'still there' } else { 'gone' })) }
$lines += ''; $lines += 'Traps (must ALWAYS be there):'
$bad = 0
foreach ($k in $traps.Keys) { $ok = & $traps[$k]; if (-not $ok) { $bad++ }; $lines += ('  {0,-42} {1}' -f $k, $(if ($ok) { 'OK' } else { 'MISSING  <-- MUST FIX' })) }
$lines += ''; $lines += $(if ($bad -eq 0) { 'RESULT: OK - no trap was touched.' } else { "RESULT: MUST FIX - $bad trap(s) removed!" })
$lines | ForEach-Object { Write-Host $_ }
$file = "C:\Results\check-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
$lines | Set-Content $file -Encoding UTF8
Write-Host "Saved: $file (on your PC: D:\AI\Purgely\sandbox-results)" -ForegroundColor Cyan
