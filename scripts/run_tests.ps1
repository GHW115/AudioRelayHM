# AudioRelayHM — 全自动跨平台测试脚本
# 用法: powershell -ExecutionPolicy Bypass -File scripts/run_tests.ps1
# 可从任意目录运行，自动定位项目根目录

$ErrorActionPreference = "Continue"
$global:Passed = 0
$global:Failed = 0
$global:Skipped = 0

# 定位项目根目录（基于脚本自身路径）
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir  # scripts/ 的父目录即项目根
Push-Location $RootDir

function Write-Step($msg) {
    Write-Host "`n=== $msg ===" -ForegroundColor Cyan
}

function Write-Pass($msg) {
    Write-Host "  [PASS] $msg" -ForegroundColor Green
    $global:Passed++
}

function Write-Fail($msg) {
    Write-Host "  [FAIL] $msg" -ForegroundColor Red
    $global:Failed++
}

function Write-Skip($msg) {
    Write-Host "  [SKIP] $msg" -ForegroundColor Yellow
    $global:Skipped++
}

# ======================== 1. Windows (.NET / xUnit) ========================
Write-Step "1. Windows xUnit Tests (dotnet test)"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    $testProj = "windows/AudioRelayWinUI.Tests/"
    $result = dotnet test $testProj --no-restore 2>&1
    if ($LASTEXITCODE -eq 0) {
        # Extract pass/fail counts from output
        $out = $result | Out-String
        if ($out -match "通过数:\s*(\d+)") { Write-Pass "Windows tests: $($Matches[1]) passed" }
        elseif ($out -match "Passed:\s*(\d+)") { Write-Pass "Windows tests: $($Matches[1]) passed" }
        else { Write-Pass "Windows tests passed" }
    } else {
        Write-Fail "Windows tests failed (exit code $LASTEXITCODE)"
        Write-Host $result -ForegroundColor DarkGray
    }
} else {
    Write-Skip "dotnet not found, skipping Windows tests"
}

# ======================== 2. C++ Ring Buffer Test ========================
Write-Step "2. C++ Ring Buffer Test (g++/clang++)"

$cpp = Get-Command g++ -ErrorAction SilentlyContinue
if (-not $cpp) { $cpp = Get-Command clang++ -ErrorAction SilentlyContinue }
if ($cpp) {
    $testSrc = "hmos/entry/src/main/cpp/ringbuffer_test.cpp"
    $testExe = "ringbuffer_test.exe"
    
    $compile = & $cpp -std=c++17 -pthread $testSrc -o $testExe 2>&1
    if ($LASTEXITCODE -eq 0 -and (Test-Path $testExe)) {
        $run = & "./$testExe" 2>&1
        $runStr = $run | Out-String
        if ($LASTEXITCODE -eq 0) {
            Write-Pass "C++ ring buffer tests passed"
        } else {
            Write-Fail "C++ ring buffer tests failed"
        }
        Write-Host $runStr -ForegroundColor DarkGray
        Remove-Item $testExe -ErrorAction SilentlyContinue
    } else {
        Write-Fail "C++ compilation failed"
        Write-Host $compile -ForegroundColor DarkGray
    }
} else {
    Write-Skip "g++/clang++ not found, skipping C++ tests"
}

# ======================== 3. HMOS ohosTest (hdc + device) ========================
Write-Step "3. HMOS ohosTest (hdc + device)"

$hdc = Get-Command hdc -ErrorAction SilentlyContinue
if (-not $hdc) {
    # Try common install paths
    $commonPaths = @(
        "$env:LOCALAPPDATA\Huawei\Sdk\hdc.exe",
        "$env:LOCALAPPDATA\Huawei\hdc.exe",
        "C:\Program Files\Huawei\DevEco Studio\sdk\default\openharmony\toolchains\hdc.exe"
    )
    foreach ($p in $commonPaths) {
        if (Test-Path $p) { $hdc = $p; break }
    }
}

if ($hdc) {
    $deviceList = & $hdc list targets 2>&1 | Out-String
    if ($deviceList -match "[a-fA-F0-9]") {
        Write-Host "  Device found: $($deviceList.Trim())"
        
        # Build required: test HAP must already exist from DevEco Studio build
        $testHapExists = Test-Path "hmos/entry/build/default/outputs/default/entry_test-default-unsigned.hap"
        if (-not $testHapExists) {
            Write-Skip "Test HAP not built yet. Build in DevEco Studio first (Build > Build HAP(s))."
        } else {
            & $hdc shell aa test -b com.example.audiorelayhmos -m entry_test -s unittest OpenHarmonyTestRunner 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Pass "HMOS tests launched (check device for results)"
            } else {
                Write-Fail "HMOS test command failed"
            }
        }
    } else {
        Write-Skip "No HMOS device connected (run: hdc list targets)"
    }
} else {
    Write-Skip "hdc not found, skipping HMOS tests`n  Install: DevEco Studio or Huawei SDK"
}

# ======================== Summary ========================
Write-Host "`n============================================" -ForegroundColor Cyan
Write-Host "Test Summary:" -ForegroundColor White
Write-Host "  Passed:  $global:Passed" -ForegroundColor Green
Write-Host "  Failed:  $global:Failed" -ForegroundColor $(if ($global:Failed -gt 0) { "Red" } else { "Gray" })
Write-Host "  Skipped: $global:Skipped" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Cyan

if ($global:Failed -gt 0) { exit 1 } else { exit 0 }
