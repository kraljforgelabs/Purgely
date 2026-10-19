# Runs inside Windows Sandbox: plants a fake program with leftovers (and "traps" that must survive), then starts Purgely.
$ErrorActionPreference = 'Continue'
Remove-Item 'C:\Results\ads-before.json' -ErrorAction SilentlyContinue   # every sandbox starts fresh
& C:\PurgelyTest\make-test-leftovers.ps1
Start-Process C:\Purgely\Purgely.exe
Write-Host ''
Write-Host 'Test plan:' -ForegroundColor Cyan
Write-Host ' 1. Clean leftovers -> "Contoso PhotoTool" (removed program) -> Scan leftovers -> Remove selected.'
Write-Host ' 2. Then run:  C:\PurgelyTest\check-after-clean.ps1   (result is saved to D:\AI\Purgely\sandbox-results)'
Write-Host ' 3. Undo center -> Restore -> run the check again: everything should be back.'
Write-Host ' 4. Same with "Microsoft OneDrive" (known-program rule): its AppData folder and registry key should be found;'
Write-Host '    your OneDrive\Documents file must survive.'
Write-Host ' 5. Ads test:  C:\PurgelyTest\check-ads.ps1  -> Bloatware -> Ads & suggestions -> Turn off ticked'
Write-Host '    -> check-ads.ps1 again -> Undo center -> Restore -> check-ads.ps1 a third time (must say "exactly as before").'
Read-Host 'Press Enter to close this window'
