# AudioRelayHMOS CLI build script (with signing injection)
# Usage: powershell -ExecutionPolicy Bypass -File scripts/build-hap.ps1 [-BuildMode debug|release] [-Target assembleHap]
# What it does:
#   1. Read hmos/signing.local.env (not committed, contains signing passwords)
#   2. Generate a temporary plain-text build-profile.json5 from the env-referenced version in repo
#      (values JSON-escaped: single backslash -> double backslash, otherwise JSON5 eats backslash escapes)
#   3. Invoke DevEco bundled hvigorw
#   4. finally restores build-profile.json5 to the env-referenced version (no plaintext in repo)
# Note: CLI hvigor does NOT resolve ${env.X} (error 00303107 observed), so this script is required;
#       DevEco Studio GUI uses the env-referenced config; run scripts/apply-signing-env.ps1 first.
param(
    [string]$BuildMode = 'debug',
    [string]$Target = 'assembleHap'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hmosDir = Join-Path $root 'hmos'
$envFile = Join-Path $hmosDir 'signing.local.env'
$configFile = Join-Path $hmosDir 'build-profile.json5'
$hvigorw = 'C:\Program Files\Huawei\DevEco Studio\tools\hvigor\bin\hvigorw.bat'
$nodeDir = 'C:\Program Files\Huawei\DevEco Studio\tools\node'

if (-not (Test-Path $envFile)) {
    Write-Host "[ERROR] signing.local.env not found: $envFile" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $hvigorw)) {
    Write-Host "[ERROR] hvigorw not found: $hvigorw" -ForegroundColor Red
    exit 1
}

# ---- read signing env ----
$signing = @{}
Get-Content $envFile | Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    $signing[$name] = $value
}

# ---- generate temporary plain-text build-profile.json5 ----
$original = Get-Content $configFile -Raw -Encoding UTF8
$temp = $original
foreach ($key in $signing.Keys) {
    $placeholder = '${env.' + $key + '}'
    $escaped = $signing[$key].Replace('\', '\\').Replace('"', '\"')
    $temp = $temp.Replace($placeholder, $escaped)
}
$unresolved = [regex]::Matches($temp, '\$\{env\.\w+\}') | ForEach-Object { $_.Value } | Select-Object -Unique
if ($unresolved) {
    Write-Host "[ERROR] unresolved env references in build-profile.json5: $unresolved" -ForegroundColor Red
    exit 1
}

try {
    Set-Content -Path $configFile -Value $temp -Encoding UTF8 -NoNewline
    Write-Host "[SIGN] temporary signing config generated; will restore after build." -ForegroundColor Green

    # ---- run build ----
    $env:PATH = "$nodeDir;$env:PATH"
    Push-Location $hmosDir
    try {
        # PS 5.1 会把 hvigor 写到 stderr 的 WARN/INFO 当 NativeCommandError 并中止脚本；
        # 调用期间临时放宽 ErrorActionPreference，用 $LASTEXITCODE 判断成败
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & $hvigorw $Target '--mode' 'module' '-p' 'product=default' '-p' "buildMode=$BuildMode" '--no-daemon'
        $code = $LASTEXITCODE
        $ErrorActionPreference = $prevEAP
        if ($code -ne 0) {
            Write-Host "[BUILD] FAILED, exit code $code" -ForegroundColor Red
            exit $code
        }
        Write-Host "[BUILD] BUILD SUCCESSFUL" -ForegroundColor Green
    } finally {
        Pop-Location
    }
} finally {
    # ---- always restore the env-referenced version ----
    Set-Content -Path $configFile -Value $original -Encoding UTF8 -NoNewline
    Write-Host "[RESTORE] build-profile.json5 restored to env-referenced (safe) version." -ForegroundColor DarkGray
}
