# Stops publishing if private records or secrets would be uploaded. ASCII only (Windows PowerShell 5.1).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bad = @()
$patterns = @('ghp_[A-Za-z0-9]{20,}', 'github_pat_[A-Za-z0-9_]{20,}', 'sk-[A-Za-z0-9]{20,}', 'AKIA[0-9A-Z]{16}',
              '-----BEGIN [A-Z ]*PRIVATE KEY-----', 'rqsty-sk-[A-Za-z0-9]{10,}', '(?i)password\s*=\s*"[^"]{4,}"')
$files = git -C $root ls-files --cached --others --exclude-standard
foreach ($f in $files) {
    if ($f -eq 'tools/secret-scan.ps1') { continue }
    if ($f -like 'private/*') { $bad += "private record would be published: $f"; continue }
    $path = Join-Path $root $f
    if (-not (Test-Path $path) -or (Get-Item $path).Length -gt 2MB) { continue }
    $text = Get-Content $path -Raw -ErrorAction SilentlyContinue
    if (-not $text) { continue }
    foreach ($p in $patterns) { if ($text -match $p) { $bad += "possible secret in $f (pattern $p)" } }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host "MUST FIX  $_" -ForegroundColor Red }; exit 1 }
Write-Host "OK        no private records or secrets in the files to publish" -ForegroundColor Green
exit 0
