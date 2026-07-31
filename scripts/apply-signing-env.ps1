# AudioRelayHMOS 签名环境变量导入脚本
# 用法: powershell -ExecutionPolicy Bypass -File scripts/apply-signing-env.ps1
# 作用: 读取 hmos/signing.local.env（不入库），将签名配置 setx 到用户环境变量，
#       build-profile.json5 通过 ${env.OHOS_*} 引用。
# 注意: setx 只对"之后启动"的进程生效，需重启 DevEco Studio 后构建。
$ErrorActionPreference = 'Stop'

$envFile = Join-Path $PSScriptRoot '..\hmos\signing.local.env'
if (-not (Test-Path $envFile)) {
    Write-Host "[错误] 未找到 $envFile`n请先从 git 历史恢复或联系仓库维护者获取签名材料。" -ForegroundColor Red
    exit 1
}

$count = 0
foreach ($line in Get-Content $envFile) {
    $line = $line.Trim()
    if ($line -eq '' -or $line.StartsWith('#')) { continue }
    $idx = $line.IndexOf('=')
    if ($idx -le 0) { continue }
    $name = $line.Substring(0, $idx).Trim()
    $value = $line.Substring($idx + 1).Trim()
    if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { continue }
    [Environment]::SetEnvironmentVariable($name, $value, 'User')
    $count++
}
Write-Host "[完成] 已导入 $count 个签名环境变量。请重启 DevEco Studio 后重新构建。" -ForegroundColor Green
