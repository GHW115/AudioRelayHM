# AudioRelayHM 双端测试自动化方案

> 当前状态：**HMOS Hypium + Windows xUnit 双端测试已建立**。
> 覆盖：AudioPacket 序列化/金丝雀、TCP 粘包解析、ADPCM 编解码、重采样。
> 缺口：NAPI C++ 层（opus_decoder.cpp — SPSC 环形缓冲/淡入淡出/延迟守卫/dlsym）尚无单元测试。

---

## 一、可测性分层

```
┌─────────────────────────────────────────────────────┐
│ Layer 4: 端到端 (硬件依赖，不可自动化)               │
│ WASAPI采集→网络→OHAudio渲染 全链路                   │
│ 仍需人工用真机测试                                    │
├─────────────────────────────────────────────────────┤
│ Layer 3: 集成测试 (部分自动化)                       │
│ UDP发送/接收、Opus编解码管线、TCP粘包处理              │
│ 可在本地回环或模拟网络下运行                          │
├─────────────────────────────────────────────────────┤
│ Layer 2: 单元测试 (完全自动化 ✓)                     │
│ 协议序列化、ADPCM编解码、重采样、环形缓冲、时钟计算     │
│ 纯逻辑，无IO，秒级运行，CI 友好                       │
├─────────────────────────────────────────────────────┤
│ Layer 1: 静态分析 (零成本，先做)                      │
│ 编译检查、类型检查、Linter                             │
└─────────────────────────────────────────────────────┘
```

**策略**：先建 Layer 1+2（最易、ROI最高），再逐步补充 Layer 3。Layer 4 无法消灭但可以缩小范围。

---

## 二、Windows 端（.NET / xUnit）✅ 已就绪

### 2.1 测试项目（已存在）

```
windows/
├── AudioRelayWinUI.csproj          ← 已有
├── MainForm.cs
└── AudioRelayWinUI.Tests/          ← ✅ 已建立
    ├── AudioRelayWinUI.Tests.csproj
    ├── AudioPacketTests.cs          ✅
    ├── AdpcmCodecTests.cs           ✅
    ├── AudioResampleTests.cs        ✅
```

### 2.2 测试项目 csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.9.0" />
    <PackageReference Include="xunit" Version="2.7.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\AudioRelayWinUI.csproj" />
  </ItemGroup>
</Project>
```

### 2.3 可测模块清单

| 被测类/方法 | 测试类型 | 测试点 | 预估用例数 |
|-------------|----------|--------|-----------|
| `AudioPacket.Serialize()` / `Deserialize()` | 往返 | 所有字段边界值、Control/AudioData、各编码、空payload | 8 |
| `AudioPacket` 与 ArkTS 端序列化一致性 | 跨端 | 固定已知字节序列验证（金丝雀测试） | 3 |
| `AdpcmCodec.Encode()` / `Decode()` | 往返 | 静音、正弦波、最大振幅、立体声 | 5 |
| `AudioCaptureService.ResampleToStereo48()` | 变换 | 48k→48k无变化、44.1k→48k、单声道→立体声、长度计算 | 4 |
| `LatencyChartPanel.AddSample()` | 状态 | 环形缓冲绕回、空数据、单样本、MAX_SAMPLES 边界 | 4 |
| `NetworkServer` TCP 粘包解析 | 解析 | 完整单包、两包粘连、半包+补全、多包粘连 | 5 |
| `BitConverter` 端序一致性 | 端序 | 与 DataView (ArkTS) 的 Int64 LE 对齐验证 | 3 |

### 2.4 运行命令

```bash
# 运行全部测试
dotnet test windows/AudioRelayWinUI.Tests/

# 带详细输出
dotnet test windows/AudioRelayWinUI.Tests/ --logger "console;verbosity=detailed"
```

---

## 三、鸿蒙端（ArkTS / Hypium）

### 3.1 项目结构（已就绪）

HarmonyOS 标准测试目录：`entry/src/ohosTest/` ✅ 已存在

```
hmos/entry/src/
├── main/        ← 已有
└── ohosTest/    ← ✅ 已建立
    ├── ets/
    │   ├── test/
    │   │   ├── AudioPacket.test.ets  ✅
    │   │   ├── AdpcmDecoder.test.ets ✅
    │   │   ├── NetworkParse.test.ets ✅
    │   │   └── List.test.ets         ✅ (聚合入口)
    │   └── testrunner/
    │       └── OpenHarmonyTestRunner.ets ✅ (Hypium TestRunner)
    ├── module.json5                  ✅ (srcEntry 已修正)
    └── resources/
```

**关键点**：`module.json5` 的 `srcEntry` 必须指向 `./ets/testrunner/OpenHarmonyTestRunner.ets`（不是旧的 `TestAbility.ets`）。该文件基于 `@ohos/hypium` 官方模板，导入 `List.test` 套件并调用 `Hypium.hypiumTest()`。

### 3.2 关键配置文件

**`ohosTest/module.json5`**:
```json5
{
  "module": {
    "name": "entry_test",
    "type": "feature",
    "srcEntry": "./ets/testability/TestAbility.ets",
    "description": "$string:entry_test_desc",
    "deliveryWithInstall": true,
    "dependencies": [
      {
        "bundleName": "com.example.audiorelayhmos",
        "moduleName": "entry"
      }
    ]
  }
}
```

### 3.3 可测模块清单

| 被测模块 | 测试点 | 预估用例数 |
|----------|--------|-----------|
| `AudioPacket.serialize()` / `deserialize()` | 往返一致性、空payload、各枚举、Int64 LE 拆分/重组 | 6 |
| `NetworkService.handleData()` | 粘包：完整单包 → 两包粘连 → 半包 → 多包 | 4 |
| `AdpcmDecoder.decode()` | 已知ADPCM字节序列 → 期望PCM输出 | 2 |
| AudioPacket 与 C# 端序列化一致性 | 固定已知十六进制字节序列验证 | 3 |

### 3.4 Hypium 使用方式

Hypium (`@ohos/hypium`) 已在 `oh_modules` 中，无需额外安装。

**入口文件 `List.test.ets`**:
```typescript
import { describe, beforeAll, beforeEach, afterEach, afterAll, it, expect } from '@ohos/hypium';
import abilityTest from './TestAbility.test.ets';

export default function testsuite() {
  describe('AudioPacket', () => { /* 由 AudioPacket.test.ets 导出 */ });
  describe('NetworkParse', () => { /* ... */ });
  describe('AdpcmDecoder', () => { /* ... */ });
}
```

### 3.5 运行方式

#### 方式一：全自动脚本（推荐）

```powershell
# Windows PowerShell — 自动检测 dotnet / g++ / hdc，按需跳过
powershell -ExecutionPolicy Bypass -File scripts/run_tests.ps1
```

脚本分 3 阶段：
1. `dotnet test` → Windows xUnit 测试
2. `g++` → C++ ring buffer 测试
3. `hdc shell aa test` → HMOS ohosTest（需连接设备 + 已构建 test HAP）

无设备时自动跳过阶段 3。

#### 方式二：DevEco Studio 一键运行

```
1. 打开 Device Manager → 启动模拟器 / 连接真机
2. 右键 ohosTest 目录 → Run 'ohosTest'
3. 结果在 Run 面板实时显示
```

#### 方式三：hdc 命令行（CI 友好）

```bash
# 先确认设备已连接
hdc list targets

# 运行测试（需先在 DevEco Studio 中 Build > Build HAP(s) 生成 test HAP）
hdc shell aa test -b com.example.audiorelayhmos -m entry_test -s unittest OpenHarmonyTestRunner
```

### 3.6 ⭐ 无需真机：本地模拟器完全可用

你的项目 build-profile.json5 已配置 `abiFilters: ["arm64-v8a", "x86_64"]`，说明已考虑模拟器运行。DevEco Studio 本地模拟器实测：

| 测试类型 | 模拟器可用 | 说明 |
|----------|-----------|------|
| 纯逻辑 UT（AudioPacket / AdpcmDecoder / 解析器） | ✅ 完美 | 不依赖硬件，秒跑 |
| NAPI C++ 加载 | ✅ 支持 | x86_64 .so 直接在模拟器运行 |
| TCP/UDP 网络连接 PC | ✅ 可连宿主机 | 模拟器内直接访问 PC 的局域网 IP |
| AudioCapturer 启动 | ⚠️ 能初始化 | 但不保证录到真实音频（虚拟麦克风可能无声源） |
| AudioRenderer 回调 | ⚠️ 能初始化 | 但音频延迟数据不可靠（虚拟时钟 ≠ 真实硬件时钟） |
| 端到端音频质量 | ❌ 不可用 | 必须真机 |

**结论**：Layer 2（单元测试）和 Layer 3（集成测试）的协议/解析/编解码部分，模拟器完全够用。你不需要每次都插真机。只有最终验证音频质量、延迟数据时才需要真机。

### 3.7 ⚠️ NAPI C++ 层测试缺口（待补充）

`opus_decoder.cpp` 中的原生音频管线暂无测试覆盖。以下模块需要补充单元测试：

| 被测模块 | 位置 | 测试点 | 可测性 |
|----------|------|--------|--------|
| `AudioRingBuffer` | `opus_decoder.cpp` | SPSC 无锁读写、满/空/回绕、`dropUntil`、`clear`、并发压力 | ✅ Host 侧编译运行 |
| `OnAudioWriteData` fade-in/out | `opus_decoder.cpp` | 淡出增益曲线、淡入恢复、边界帧数 | ⚠️ 需 mock OHAudio |
| 延迟守卫 `LATENCY_GUARD_MS` | `opus_decoder.cpp` | 超阈值丢帧、对齐声道 | ⚠️ 需构造 buffer 状态 |
| `LoadAudioApis()` dlsym | `opus_decoder.cpp` | 成功/失败分支、nullptr guard | ⚠️ 依赖真实 .so |
| `OnAudioInterrupt` 回调 | `opus_decoder.cpp` | PAUSE→RESUME 恢复逻辑 | ⚠️ 需 mock 系统事件 |

**优先级**：`AudioRingBuffer` 最优先（纯逻辑，Host 侧可跑）。其余依赖 OHAudio mock/真实环境，暂缓。

### 3.8 新增 C++ Host 侧测试

```bash
# 在 hmos/entry/src/main/cpp/ 创建 ringbuffer_test.cpp
# 更新 CMakeLists.txt 添加测试 target（exe, 不链接 OHAudio）
# 编译并运行
cd hmos/entry/src/main/cpp
cmake -B build && cmake --build build
./build/ringbuffer_test
```

测试用例：
- `write(10), read(10)` 往返一致性
- `write(2*capacity)` 满缓冲写截断
- `read(100)` 空缓冲补零 + underrunEpoch++
- `dropUntil(target)` 丢弃正确字节数
- `clear()` 后 available() = 0
- 并发：writer 线程高频 write，reader 线程周期 read，读写总量一致

---

## 四、重点测试场景详解

### 4.1 协议跨端一致性测试（最关键）

这是整个项目最容易出 bug 的地方——两端各自解析 44 字节包头，端序/偏移一旦不一致就静默出错。

**方案**：生成一组「金丝雀」十六进制字节序列，两端都用 `deserialize()` 解析后比对结果。

```
金丝雀 1: PCM AudioData 包
  字节: 01 FF 00 00 00 00 00 01 00 00 00 00 00 00 00 00 
        (TS低32) (TS高32) (encTS低32) (encTS高32)
        (sendTS低32) (sendTS高32) 80 BB 00 00 02 10 00 00 
        08 00 00 00 [8字节PCM 0000 0000 0000 0000]

金丝雀 2: CONFIG 控制包 (Opus 64k, buffer 200ms)
  字节: 00 06 FF 01 ... payload: 01 40 00 00 00 C8 00 00 00

金丝雀 3: TIME_SYNC 包
```

两端都跑同样的金丝雀，出来的 `AudioPacket` 各字段必须完全一致。

### 4.2 TCP 粘包解析测试

抓取 TCP 接收缓冲区累积解析逻辑——这是手机端最容易出 bug 的部分（C# 端同理）。

```
场景1: 缓冲区已有 Header+payload完整 → 应正确解析1包，剩余0
场景2: 缓冲区=包1完整+包2完整 → 应出2包，剩余0
场景3: 缓冲区=包1完整+包2半截 → 应出1包，剩余半截保留
场景4: 缓冲区=半截头 → 应出0包，等待更多数据
场景5: 缓冲区=空 → 无操作
```

### 4.3 ADPCM 编解码往返

```
输入: 已知的 short[] (如 1kHz 正弦波 @ 48kHz 1秒)
编码: AdpcmCodec.Encode()
解码: AdpcmCodec.Decode()
验证: 输出 PCM 与输入的最大偏差 < 可接受阈值
```

---

## 五、CI/CD 流水线设计

### 5.1 GitHub Actions（推荐）

```yaml
name: Test

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  windows-tests:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Build & Test
        run: dotnet test windows/AudioRelayWinUI.Tests/ --logger trx

  # 鸿蒙端 CI 可选方案（按成本递增排序）：
  #
  # 方案 A（零成本）：本地 pre-commit hook
  #   每次 git commit 前手动跑一次模拟器测试
  #
  # 方案 B（推荐）：自建 GitHub Actions Runner
  #   在安装了 DevEco Studio + 模拟器的 Windows 机器上注册 Runner
  #   流水线自动启动模拟器 → 安装 test HAP → hdc aa test → 收集结果
  #
  # 方案 C（云端）：华为 DevEco Testing 云服务
  #   如果华为提供云端模拟器/真机测试服务，可直接接入
```

### 5.2 本地 Pre-commit Hook（推荐，零成本启动）

```bash
# .git/hooks/pre-commit
#!/bin/bash
set -e

echo "=== Windows: Build & Test ==="
dotnet test windows/AudioRelayWinUI.Tests/ -c Release

echo "=== HarmonyOS: Build test HAP ==="
# 需要 DevEco Studio 命令行工具或 hvigorw
cd hmos
hvigorw assembleHap -p buildMode=debug -p module=entry@ohosTest
cd ..

echo ""
echo "✅ 自动检查通过"
echo "💡 下一步：在 DevEco Studio 模拟器上 Run 'ohosTest' 验证鸿蒙端"
```

> **注意**：鸿蒙端测试需要模拟器运行时环境，pre-commit hook 只能做到「编译通过」。完整自动化需要方案 B（自建 Runner）。

---

## 六、仍需人工验证的清单（模拟器接管后的剩余项）

模拟器 + 单元测试覆盖后，**真机只需要验证以下 6 项**：

| 验证项 | 设备 | 频率 | 方法 |
|--------|------|------|------|
| PC→手机 音频无杂音/断流 | 真机 | 每次大改动 | 连接后播放音乐 2 分钟 |
| 手机→PC 虚拟麦克风可用 | 真机+VB-Cable | 每次改 AudioCapture | 会议软件录音回放 |
| 编码切换 PCM↔Opus 不崩溃 | 真机 | 每次改编码器 | 运行时切换编码并确认音频连续 |
| 熄屏后音频不中断 | 真机 | 每次改 AVSession | 锁屏后等待 1 分钟 |
| Wi-Fi 切换/弱网恢复 | 真机 | 每次改网络层 | 开关飞行模式再恢复 |
| 不同采样率声卡兼容 | PC | 每次改 WASAPI | 在 44.1k/96k 设备上测试 |

**不需要真机的**（模拟器/脚本跑）：
- AudioPacket 序列化往返 ✓
- 跨端协议一致性 ✓
- 粘包解析 ✓
- ADPCM 编解码 ✓
- C++ SPSC 环形缓冲 ✓ (scripts/run_tests.ps1)
- TCP/UDP 连接建立/断开 ✓

---

## 七、实施步骤（✅ 已完成）

| 步骤 | 内容 | 状态 |
|------|------|------|
| **Step 1** | 创建 `windows/AudioRelayWinUI.Tests/` 项目 + AudioPacket 往返测试 | ✅ |
| **Step 2** | Windows 端 ADPCM、重采样测试 | ✅ |
| **Step 3** | 跨端金丝雀序列化一致性测试（C# + ArkTS 双端） | ✅ |
| **Step 4** | 创建 `hmos/entry/src/ohosTest/` 目录 + Hypium 配置 | ✅ |
| **Step 5** | 手机端 AudioPacket、粘包解析、AdpcmDecoder 测试 | ✅ |
| **Step 6** | 创建自动化测试脚本 + 修正 TestRunner | ✅ |

---

## 八、自动化测试脚本

`scripts/run_tests.ps1` — 一键运行所有可自动化测试：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run_tests.ps1
```

环境前置条件：
- **Windows 测试**：需要 .NET 8 SDK
- **C++ ring buffer 测试**：需要 g++ 或 clang++（在 PATH 中）
- **HMOS 测试**：需要 hdc 在 PATH 中 + 设备已连接 + test HAP 已构建（首次需 DevEco Studio Build HAP(s)）

脚本自动检测缺失工具并跳过对应阶段，最后打印彩色汇总（pass/fail/skip）。CI 友好（exit code 0 = 全部通过）。

---

## 九、快速开始

```powershell
# 1. 运行全自动测试
powershell -ExecutionPolicy Bypass -File scripts/run_tests.ps1

# 2. DevEco Studio → 右键 ohosTest → Run（首次构建 test HAP）

# 3. hdc 命令行（需设备连接 + test HAP 已构建）
hdc shell aa test -b com.example.audiorelayhmos -m entry_test -s unittest OpenHarmonyTestRunner
```
