# Setup local plain-text signing config for DevEco Studio GUI builds
# Usage: powershell -ExecutionPolicy Bypass -File scripts/setup-local-signing.ps1
# Why: this SDK's hvigor does NOT resolve ${env.X} in build-profile.json5 (error 00303107
#      observed in both CLI and GUI). The repo keeps the env-referenced version for safety;
#      this script generates a local plain-text version (from hmos/signing.local.env) and
#      marks it skip-worktree so git never tracks or commits the secrets.
# After running: restart/refresh DevEco Studio, then build as usual.
# To rotate certs: edit hmos/signing.local.env, re-run this script, restart DevEco.
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hmosDir = Join-Path $root 'hmos'
$envFile = Join-Path $hmosDir 'signing.local.env'
$configFile = Join-Path $hmosDir 'build-profile.json5'

if (-not (Test-Path $envFile)) {
    Write-Host "[ERROR] signing.local.env not found: $envFile" -ForegroundColor Red
    exit 1
}

# ---- read signing env ----
$signing = @{}
Get-Content $envFile | Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    $signing[$name] = $value
}

# ---- template = env-referenced version from git (always clean) ----
$template = git show "HEAD:hmos/build-profile.json5" 2>$null
if ($LASTEXITCODE -ne 0 -or -not $template) {
    Write-Host "[ERROR] cannot read HEAD:hmos/build-profile.json5" -ForegroundColor Red
    exit 1
}

# ---- substitute ${env.X} with JSON-escaped values ----
$temp = $template
foreach ($key in $signing.Keys) {
    $placeholder = '${env.' + $key + '}'
    # JSON string escaping: single backslash -> double backslash, quote -> \"
    $escaped = ($signing[$key] -replace '\\', '\\') -replace '"', '\"'
    $temp = $temp.Replace($placeholder, $escaped)
}
$unresolved = [regex]::Matches($temp, '\$\{env\.\w+\}') | ForEach-Object { $_.Value } | Select-Object -Unique
if ($unresolved) {
    Write-Host "[ERROR] unresolved env references: $unresolved" -ForegroundColor Red
    exit 1
}

Set-Content -Path $configFile -Value $temp -Encoding UTF8 -NoNewline
git update-index --skip-worktree $configFile
Write-Host "[OK] local plain-text signing config written to build-profile.json5 (skip-worktree set)." -ForegroundColor Green
Write-Host "[OK] Restart/refresh DevEco Studio, then build. Secrets stay out of git." -ForegroundColor Green
