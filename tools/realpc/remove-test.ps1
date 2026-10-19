# Removes ONLY what plant.ps1 created (listed in sandbox-results\realpc-created.txt), plus Purgely's Undo
# packages of the test programs are left for you to discard in the Undo center.
$results = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'sandbox-results'
$manifest = Join-Path $results 'realpc-created.txt'
if (-not (Test-Path $manifest)) { Write-Host 'Nothing to remove - no test was planted.' -ForegroundColor Yellow; exit 0 }
$allowed = @("$env:ProgramFiles\PurgelyTest Contoso", "$env:APPDATA\Contoso PhotoTool", "$env:LOCALAPPDATA\PhotoTool",
  "$env:USERPROFILE\PurgelyTest Trap", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Contoso PhotoTool",
  "$env:LOCALAPPDATA\Dropbox", "$env:USERPROFILE\Dropbox")
foreach ($line in Get-Content $manifest) {
  $kind, $what = $line -split '\|', 2
  switch ($kind) {
    'DIR' {
      if ($allowed -notcontains $what) { Write-Host "Skipped (not a test path): $what" -ForegroundColor Yellow; continue }
      if (Test-Path $what) {
        # Junctions first, without following them
        Get-ChildItem $what -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue | ForEach-Object { cmd /c rmdir "$($_.FullName)" }
        Remove-Item $what -Recurse -Force -ErrorAction SilentlyContinue
      }
    }
    'KEY'  { if ($what -match 'PurgelyTest|\\Software\\Contoso$') { Remove-Item $what -Recurse -Force -ErrorAction SilentlyContinue } }
    'RUN'  { Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PhotoToolUpdater -ErrorAction SilentlyContinue }
    'TASK' { schtasks /Delete /TN $what /F 2>$null | Out-Null }
  }
  Write-Host "Removed: $what"
}
Remove-Item $manifest
Remove-Item (Join-Path $results 'realpc-ads-before.json') -ErrorAction SilentlyContinue
Write-Host ''
Write-Host 'All test items removed. In Purgely''s Undo center you can Discard the test packages (Contoso PhotoTool, Dropbox).' -ForegroundColor Green
