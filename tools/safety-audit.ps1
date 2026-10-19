# Purgely safety audit: finds destructive API calls and compares them with the reviewed baseline.
param([switch]$UpdateBaseline)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src\Purgely'
$baselineFile = Join-Path $PSScriptRoot 'destructive-calls.json'

$patterns = [ordered]@{
    'File.Delete'          = 'File\.Delete\('
    'Directory.Delete'     = 'Directory\.Delete\('
    'Directory.Move'       = 'Directory\.Move\('
    'File.Move'            = 'File\.Move\('
    'DeleteSubKey'         = 'DeleteSubKey(Tree)?\('
    'DeleteValue'          = 'DeleteValue\('
    'SetValue'             = '\.SetValue\('
    'reg delete'           = 'reg", \$?"delete'
    'reg import'           = 'reg", \$?"import'
    'sc delete/config/stop'= 'sc", \$?"(delete|config|stop)'
    'schtasks change'      = 'schtasks", \$?"/(Delete|Change|Create)'
    'Process.Kill'         = '\.Kill\('
    'MoveFileEx'           = 'MoveFileEx|DeleteOnReboot\('
    'Recycle/DeleteBestEffort' = 'DeleteBestEffort\('
    'pnputil (drivers)'    = 'pnputil\.exe", \$?"/(add|delete)-driver'
    'Remove-AppxPackage'   = 'Remove-AppxPackage -Package'
}

$current = [ordered]@{}
Get-ChildItem -Path $src -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length + 1)
    $text = Get-Content $_.FullName -Raw
    foreach ($name in $patterns.Keys) {
        $count = ([regex]::Matches($text, $patterns[$name])).Count
        if ($count -gt 0) { $current["$rel|$name"] = $count }
    }
}

if ($UpdateBaseline) {
    $current | ConvertTo-Json | Set-Content -Path $baselineFile -Encoding UTF8
    Write-Host "Baseline updated: $($current.Count) entries." -ForegroundColor Green
    exit 0
}

$baseline = @{}
if (Test-Path $baselineFile) {
    (Get-Content $baselineFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $baseline[$_.Name] = [int]$_.Value }
}

$must = @(); $check = @()
foreach ($key in $current.Keys) {
    $file, $name = $key -split '\|', 2
    if (-not $file.StartsWith('Core\')) {
        $must += "Destructive call '$name' in UI code ($file) - move it into Core (Cleaner/StartupManager)."
    }
    $old = if ($baseline.ContainsKey($key)) { $baseline[$key] } else { 0 }
    if ($current[$key] -gt $old) {
        $check += "New '$name' call(s) in $file ($old -> $($current[$key])) - needs council review, then run: tools\safety-audit.ps1 -UpdateBaseline"
    }
}

Write-Host ''
Write-Host '=== Safety audit ===' -ForegroundColor Cyan
foreach ($m in $must)  { Write-Host "MUST FIX  $m" -ForegroundColor Red }
foreach ($c in $check) { Write-Host "CHECK     $c" -ForegroundColor Yellow }
if ($must.Count -gt 0) { exit 1 }
if ($check.Count -gt 0) { exit 2 }
Write-Host "OK        $($current.Count) reviewed destructive call sites, no new ones." -ForegroundColor Green
exit 0
