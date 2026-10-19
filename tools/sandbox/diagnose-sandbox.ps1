# Collects why Windows Sandbox disconnects. Read-only: changes nothing. Report goes to sandbox-results.
$out = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'sandbox-results\sandbox-diagnosis.txt'
$r = @()
$r += "=== Windows ==="
$os = Get-CimInstance Win32_OperatingSystem
$r += "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
$r += "Free memory MB: $([math]::Round($os.FreePhysicalMemory/1024))  Total MB: $([math]::Round($os.TotalVisibleMemorySize/1024))"
$c = Get-PSDrive C; $r += "Free disk C: GB: $([math]::Round($c.Free/1GB,1))"
$r += ""; $r += "=== Virtualization ==="
$cs = Get-CimInstance Win32_ComputerSystem
$r += "HypervisorPresent: $($cs.HypervisorPresent)"
$p = Get-CimInstance Win32_Processor | Select-Object -First 1
$r += "CPU: $($p.Name)  VirtualizationFirmwareEnabled: $($p.VirtualizationFirmwareEnabled)"
$r += ""; $r += "=== Optional features ==="
foreach ($f in 'Containers-DisposableClientVM','Microsoft-Hyper-V-All','Microsoft-Hyper-V-Hypervisor','VirtualMachinePlatform','HypervisorPlatform','Microsoft-Windows-Subsystem-Linux','Containers') {
  $s = Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue
  $r += ('{0,-36} {1}' -f $f, $(if ($s) { $s.State } else { 'not found' }))
}
$r += ""; $r += "=== Services ==="
foreach ($s in 'CmService','vmcompute','hns','vmms','HvHost','WSAIFabricSvc') {
  $x = Get-Service $s -ErrorAction SilentlyContinue
  $r += ('{0,-14} {1}' -f $s, $(if ($x) { "$($x.Status) / $($x.StartType)" } else { 'not installed' }))
}
$r += ""; $r += "=== Device Guard / memory integrity ==="
$dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction SilentlyContinue
if ($dg) { $r += "VBS status: $($dg.VirtualizationBasedSecurityStatus)  Services running: $($dg.SecurityServicesRunning -join ',')" }
$r += ""; $r += "=== Other virtualization software ==="
$names = 'VirtualBox','VMware','BlueStacks','LDPlayer','Nox','MEmu','Docker','Android Studio','Avast','AVG','Kaspersky','Bitdefender','Norton','McAfee'
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
  Where-Object { $n = $_.DisplayName; $n -and ($names | Where-Object { $n -like "*$_*" }) } | ForEach-Object { $r += "  $($_.DisplayName) $($_.DisplayVersion)" }
$r += ""; $r += "=== Sandbox app ==="
Get-AppxPackage *WindowsSandbox* -ErrorAction SilentlyContinue | ForEach-Object { $r += "  $($_.Name) $($_.Version)" }
$r += ""; $r += "=== Recent errors (last 2 hours) ==="
$since = (Get-Date).AddHours(-2)
foreach ($log in 'Microsoft-Windows-Hyper-V-Compute-Admin','Microsoft-Windows-Hyper-V-Worker-Admin','Microsoft-Windows-Hyper-V-Hypervisor-Admin','Microsoft-Windows-Containers-Wcifs/Operational','Application','System') {
  try {
    Get-WinEvent -FilterHashtable @{ LogName = $log; Level = 1,2,3; StartTime = $since } -MaxEvents 15 -ErrorAction Stop |
      Where-Object { $log -notin 'Application','System' -or $_.ProviderName -match 'Hyper-V|Sandbox|vmcompute|Container|Hypervisor|Wcifs|CmService|WindowsSandbox' } |
      ForEach-Object { $r += "[$log] $($_.TimeCreated.ToString('HH:mm:ss')) $($_.ProviderName) #$($_.Id): $(($_.Message -split "`n")[0])" }
  } catch { }
}
$r | Set-Content $out -Encoding UTF8
Write-Host "Report saved: $out" -ForegroundColor Green
