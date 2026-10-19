param([string]$Results = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'sandbox-results'))
# Ads, AI & privacy test: the first run saves how the switches were; later runs compare with it.
# Run it 1) before "Turn off ticked", 2) after it (values change), 3) after Undo center -> Restore (must equal run 1).
$cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
$adv = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$values = @(
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'Start_IrisRecommendations'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SubscribedContent-338388Enabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SystemPaneSuggestionsEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SilentInstalledAppsEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'PreInstalledAppsEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'OemPreInstalledAppsEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SubscribedContent-338393Enabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SubscribedContent-353694Enabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SubscribedContent-353696Enabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SoftLandingEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement', 'ScoobeSystemSettingEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'RotatingLockScreenOverlayEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager', 'SubscribedContent-338387Enabled'),
  @('HKCU:\Software\Policies\Microsoft\Windows\Explorer', 'DisableSearchBoxSuggestions'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo', 'Enabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Privacy', 'TailoredExperiencesWithDiagnosticDataEnabled'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'Start_AccountNotifications'),
  @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'TaskbarDa'),
  @('HKCU:\Software\Policies\Microsoft\Windows\WindowsAI', 'DisableAIDataAnalysis'),
  @('HKCU:\Software\Policies\Microsoft\Windows\WindowsAI', 'DisableClickToDo'),
  @('HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot', 'TurnOffWindowsCopilot'),
  @('HKCU:\Software\Policies\Microsoft\Edge', 'HubsSidebarEnabled'),
  @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection', 'AllowTelemetry'),
  @('HKCU:\Software\Microsoft\InputPersonalization', 'RestrictImplicitTextCollection'),
  @('HKCU:\Software\Microsoft\InputPersonalization', 'RestrictImplicitInkCollection'),
  @('HKCU:\Software\Microsoft\InputPersonalization\TrainedDataStore', 'HarvestContacts'),
  @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\System', 'PublishUserActivities'),
  @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\System', 'UploadUserActivities'),
  @('HKCU:\Software\Microsoft\Siuf\Rules', 'NumberOfSIUFInPeriod'))
$now = [ordered]@{}
foreach ($v in $values) {
  $x = (Get-ItemProperty -Path $v[0] -Name $v[1] -ErrorAction SilentlyContinue).($v[1])
  $now["$($v[0])|$($v[1])"] = $(if ($null -eq $x) { '(missing)' } else { [string]$x })
}
$baseFile = Join-Path $Results 'realpc-ads-before.json'
if (-not (Test-Path $baseFile)) {
  $now | ConvertTo-Json | Set-Content $baseFile -Encoding UTF8
  Write-Host "Saved how the switches are now ($($now.Count) values). Now turn them off in Purgely and run this again." -ForegroundColor Cyan
  exit
}
$before = @{}
(Get-Content $baseFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $before[$_.Name] = $_.Value }
$changed = 0
$lines = @("Ads & suggestions check - $(Get-Date -Format 'yyyy-MM-dd HH:mm')", '')
foreach ($k in $now.Keys) {
  $same = $before[$k] -eq $now[$k]
  if (-not $same) { $changed++ }
  $lines += ('  {0,-4} {1,-45} before: {2,-10} now: {3}' -f $(if ($same) { '' } else { '*' }), ($k -split '\|')[1], $before[$k], $now[$k])
}
$lines += ''
$lines += $(if ($changed -eq 0) { 'RESULT: everything is exactly as before (correct after Undo).' } else { "RESULT: $changed value(s) differ from before (correct after 'Turn off', MUST FIX after Undo)." })
$lines | ForEach-Object { Write-Host $_ }
$file = Join-Path $Results "realpc-ads-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
$lines | Set-Content $file -Encoding UTF8
Write-Host "Saved: $file" -ForegroundColor Cyan
