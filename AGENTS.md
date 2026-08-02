# AGENTS.md — AudioRelayHM 项目文档

> **用途**：本文件供 AI 编程助手（Claude、Copilot、Qoder 等）快速理解项目全貌。
> 所有信息反映项目当前实际状态，而非初始设计。

---

## 一、项目定位

**AudioRelayHM** 是一款 **HarmonyOS NEXT ↔ Windows PC 双向实时音频串流工具**。

- 将 PC 系统音频实时推送到鸿蒙手机播放（PC → 手机）
- 将手机麦克风音频实时推送到 PC，作为**虚拟麦克风**供会议软件使用（手机 → PC）
- 支持 PCM / Opus 编码切换，缓冲时长可配置
- 支持端到端延迟分项测量与实时曲线显示

---

## 二、核心功能

### 2.1 双向音频串流

| 方向 | 含义 | 触发条件 | 输出目标 |
|------|------|----------|----------|
| **PC → 手机** | PC 系统音频 → 手机播放 | 连接建立后默认自动启动 | 手机扬声器 |
| **手机 → PC** | 手机麦克风 → PC 虚拟输入 | 用户显式选择 VB-Cable 设备后启动 | VB-Cable 虚拟麦克风 |

### 2.2 编码与缓冲

| 项目 | 值 |
|------|-----|
| 默认编码 | **PCM**（零编解码延迟） |
| 可选编码 | Opus（32k ~ 192kbps 可调） |
| 默认缓冲 | **0ms**（无缓冲模式） |
| 缓冲范围 | 200ms ~ 2000ms（手机端 UI 可调） |
| 音频参数 | 48kHz / 2ch / 16bit S16LE（192,000 B/s） |

### 2.3 延迟测量与显示

- **端到端延迟**：6 环节组成（WASAPI采集→编码→网络→解码→AudioRenderer→缓冲补偿）
- **延迟分项显示**：PC 端堆叠面积图，4 个物理分项：
  - **PC 处理**（蓝）：encodeTime - captureTime（已含 WASAPI 30ms 采集排队；Opus 模式另 +20ms 帧对齐时长）
  - **网络传输**（绿）：phoneNow - sendTime - clockOffset
  - **缓冲等待**（橙）：AudioRenderer 队列等待
  - **渲染**（紫）：解码 + AudioRenderer 硬件渲染
- **时钟校准**：NTP 风格 TIME_SYNC 协议消除双端时钟偏差
- **图表控件**：WinForms GDI+ 自绘，环形缓冲区 300 点 @ 200ms = 60 秒窗口

### 2.4 虚拟麦克风（手机→PC）

- 依赖 **VB-Cable** 虚拟音频驱动（用户自行安装）
- PC 端将手机音频输出至 **"CABLE Input"** 设备
- 第三方应用选择 **"CABLE Output"** 作为麦克风

---

## 三、技术架构

### 3.1 双协议网络

| 协议 | 端口 | 用途 |
|------|------|------|
| **TCP** | 9287（可配置） | 控制消息：握手/配置/时钟同步/心跳/延迟报告 |
| **UDP** | 9288 | 双向音频数据传输 |

- 端口在设置页面配置，启动时自动读取
- PC 端作为 TCP Server 监听，手机端主动连接

### 3.2 音频管线（PC → 手机）

```
Windows WASAPI Loopback → float→short 转换 → [Opus编码] → UDP → 
手机 NAPI C++ 层 → UDP接收 → [Opus解码] → OHAudio AudioRenderer → 扬声器
```

- 手机端 PC→手机 方向已完全下沉至 **NAPI C++ 原生管线**（UDP接收 + Opus解码 + OHAudio渲染）
- AudioPlay.ets 仅保留 AVSession + 后台任务管理

### 3.3 音频管线（手机 → PC）

```
手机 AudioCapturer (readData回调) → PCM → UDP → 
PC BufferedWaveProvider → WaveOutEvent → 虚拟音频设备
```

- 手机端通过 `AudioCapturer.on('readData')` 回调获取麦克风数据
- 通过 UDP socket 发送至 PC

### 3.4 控制协议命令

| 命令 | 枚举值 | 方向 | 说明 |
|------|--------|------|------|
| HANDSHAKE | 0 | Phone→PC | 握手请求（ payload: "AudioRelayHMOS"） |
| HANDSHAKE_ACK | 1 | PC→Phone | 握手响应 |
| HEARTBEAT | 2 | 双向 | 心跳保活（1s 间隔） |
| START_STREAM | 3 | 双向 | 开始音频流 |
| STOP_STREAM | 4 | 双向 | 停止音频流 |
| VOLUME | 5 | 双向 | 音量设置 |
| CONFIG | 6 | Phone→PC | 配置下发（9字节：encoding+bitrate+bufferMs） |
| LATENCY_REPORT | 7 | Phone→PC | 延迟分项报告 |
| TIME_SYNC | 8 | 双向 | 时钟同步（NTP风格） |

### 3.5 网络包格式（AudioPacket）

- **包头大小**：**44 字节**（已从原始 28 字节扩展）
- **字段**：msgType(1) + command(1) + direction(1) + encodingType(1) + sequence(4) + timestamp(8) + encodeTimestamp(8) + sendTimestamp(8) + sampleRate(4) + channels(1) + bitsPerSample(1) + reserved(2) + payloadLength(4)
- **负载**：紧跟包头，长度由 payloadLength 指定

---

## 四、双端职责划分

| 职责 | 手机端（主控） | PC 端（被控） |
|------|---------------|---------------|
| 参数控制 | **全权控制**编码/码率/缓冲 | 响应 CONFIG 命令，不保留本地配置界面 |
| 连接发起 | 主动连接 PC | TCP Server 监听等待 |
| UI | 主页面 + 参数选择器 | 服务器页 + 播放器页 + 设置页 + 延迟图表 |
| 配置入口 | 编码/码率/缓冲选择器 | 端口号配置 |

> **重要决策**：由鸿蒙手机端 UI 全权控制所有参数，PC 端仅作为被控端，不保留本地配置界面或默认值覆盖逻辑。

---

## 五、关键约束与已知缺陷

### 5.1 手机→PC 方向安全规则（必须遵守）

- ❌ **禁止**在任何情况下将手机→PC 音频输出到物理扬声器
- ✅ 仅在用户显式选择虚拟音频设备（如 VB-Cable）后才启动播放
- ✅ 默认设备（索引0）被选中时，强制停止手机→PC 播放

### 5.2 已移除的静音保活逻辑（切勿恢复）

- ❌ 静音预热（burst 写入）
- ❌ 静音保活填充
- ❌ 基于静音量的反馈修正
- ✅ 当前为**纯数据驱动**：`write()` 仅由真实音频数据触发，无数据不写入
- **原因**：静音保活在实时音频流中破坏时间同步和缓冲模型

### 5.3 PCM↔Opus 编码切换竞态（已知缺陷）

- **问题**：`Stop()` 将 `opusEncoder` 置 null 后，`encodingType` 已更新为 Opus，若 `OnDataAvailable` 回调在此间隙触发，会访问 null 的 `opusEncoder` 导致 NullReferenceException
- **当前状态**：使用 PCM 默认编码时不受影响，运行时切换需谨慎

### 5.4 熄屏音频保活（必须保持的机制）

- 使用 **STREAM_USAGE_GAME**（高优先级，不被B站等视频流 Duck）
- 中断模式：**INDEPENDENT_MODE**（独立模式，不参与音频焦点竞争）
- AVSession 完整初始化顺序：创建 → setAVMetadata → 注册 play/pause → activate → setAVPlaybackState
- **AVSession 必须在 AudioRenderer 之前创建**
- 后台任务：`backgroundModes: ["audioPlayback", "audioRecording", "dataTransfer"]`
- Worker 线程处理 UDP 接收，不受熄屏降频影响

---

## 六、编码约定

### 6.1 协议与序列化

| 约定 | 说明 |
|------|------|
| **timestamp 格式** | Int64 LE（低32位 + 高32位），跨端统一。ArkTS 端用 `>>>0` 和 `/ 0x100000000` 拆分/重组 |
| **TCP 解析** | 按 `payloadLen`（偏移40-43，小端序）进行**长度驱动解析**，累积缓冲区 + 粘包/拆包处理 |
| **编码类型枚举** | PCM=0, Opus=1, ADPCM=2 |
| **流方向枚举** | PC_TO_PHONE=0, PHONE_TO_PC=1 |

### 6.2 ArkTS 端

| 约定 | 说明 |
|------|------|
| **显式类型声明** | 禁止隐式 `any`/`unknown`，所有变量/参数/返回值必须有明确类型 |
| **NAPI 模块导入** | 使用 `declare module` + 解构导入：`import { nativeAudioInit } from 'libopus_decoder.so'` |
| **NAPI 桥接一致** | `OpusDecoderBridge.ets` 中的函数声明必须与 `opus_decoder.cpp` 导出的 NAPI 函数**严格一致** |

### 6.3 C++ 端（NAPI）

| 约定 | 说明 |
|------|------|
| **OHAudio 头文件** | 按模块拆分包含（如 `native_audiorenderer.h`, `native_audiocapturer.h`） |
| **音频流类型** | 固定 `AUDIOSTREAM_USAGE_GAME` |
| **采样格式** | `AUDIOSTREAM_SAMPLE_S16LE` |
| **编码类型** | `AUDIOSTREAM_ENCODING_TYPE_RAW` |
| **中断模式** | `OH_AudioStreamBuilder_SetRendererInterruptMode` 设置为 `INDEPENDENT_MODE`（独立模式，不参与焦点竞争） |

### 6.4 C# 端（Windows）

| 约定 | 说明 |
|------|------|
| **Opus 编码器** | ⚠️ 当前代码仍使用已过时的 `new OpusEncoder()`，需迁移至 `OpusCodecFactory.CreateEncoder()` + `Span<byte>` 重载 |
| **UI 线程安全** | 跨线程更新 UI 必须使用 `Invoke()`，延迟图表 `AddSample()` 使用 `lock` 保护 |
| **WASAPI 采集** | 事件驱动模式（EventCallback），bufferPeriod=30ms，异步触发 DataAvailable |
| **音频参数对齐** | 从 `WasapiLoopbackCapture.WaveFormat` 获取实际参数，必要时重采样+声道下混至 48kHz/2ch/S16LE。已匹配时零开销短路 |
| **发布命令** | `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` |
| **发布路径** | `windows/bin/Release/net8.0-windows/win-x64/publish/AudioRelayWinUI.exe`（注意是 `net8.0-windows`，不是 `net8.0`） |

### 6.5 构建配置

| 约定 | 说明 |
|------|------|
| **hvigor-config.json5** | 必须置于 `hmos/hvigor/` 子目录，`modelVersion` 与 `oh-package.json5` 保持一致 |
| **hvigor modelVersion** | 通过 `hvigor-config.json5` 显式配置 |
| **signingConfigs** | 使用 HarmonyOS 自动签名（Debug），**证书路径与密码不入库**：`build-profile.json5` 通过 `${env.OHOS_*}` 环境变量引用，本地值存于 gitignored 的 `hmos/signing.local.env`，用 `scripts/apply-signing-env.ps1` 导入（改证书后需重启 DevEco） |
| **鸿蒙编译** | DevEco Studio 打开 `hmos/` 目录；命令行构建用 `powershell -ExecutionPolicy Bypass -File scripts/build-hap.ps1`（自动注入本地签名并恢复安全配置，**不要**直接跑 hvigorw——命令行 hvigor 不解析 `${env.X}` 且 JSON 单反斜杠会被吃掉）。**GUI 构建**：本 SDK 的 hvigor 同样不解析 `${env.X}`，需先跑 `scripts/setup-local-signing.ps1` 生成本地明文配置（自动 `git update-index --skip-worktree`，明文不入库），然后重启/刷新 DevEco 直接构建 |
| **ohpm** | 依赖锁定在 `oh-package-lock.json5` |

---

## 七、项目文件索引

### 7.1 手机端核心文件

| 文件 | 职责 |
|------|------|
| `hmos/entry/src/main/ets/pages/Index.ets` | 主页面 UI：连接、参数选择、延迟显示 |
| `hmos/entry/src/main/ets/model/AudioPacket.ets` | 网络包模型：序列化/反序列化（44字节包头） |
| `hmos/entry/src/main/ets/service/NetworkService.ets` | TCP 控制 + UDP 音频发送，粘包处理，自动重连 |
| `hmos/entry/src/main/ets/service/AudioPlay.ets` | NAPI 原生管线控制 + AVSession + 后台任务 |
| `hmos/entry/src/main/ets/service/AudioCapture.ets` | 麦克风采集（AudioCapturer readData 回调） |
| `hmos/entry/src/main/ets/service/OpusDecoderBridge.ets` | NAPI 模块类型声明（与 C++ 导出函数一一对应） |
| `hmos/entry/src/main/cpp/opus_decoder.cpp` | NAPI C++：UDP接收 + Opus解码 + OHAudio渲染管线 |
| `hmos/entry/src/main/cpp/CMakeLists.txt` | C++ 编译配置 |
| `hmos/entry/src/main/module.json5` | 模块配置：权限、abilities、backgroundModes |
| `hmos/AppScope/app.json5` | 应用清单：包名、版本、图标 |

### 7.2 PC 端核心文件

| 文件 | 职责 |
|------|------|
| `windows/MainForm.cs` | 主窗体：UI布局、NetworkServer、音频捕获/播放、延迟图表 |
| `windows/AudioRelayWinUI.csproj` | .NET 项目文件：目标框架、NuGet依赖、发布选项 |

### 7.3 构建配置文件

| 文件 | 职责 |
|------|------|
| `hmos/build-profile.json5` | 鸿蒙构建配置：签名、SDK版本、产品定义 |
| `hmos/hvigor/hvigor-config.json5` | hvigor modelVersion 配置 |
| `hmos/hvigorfile.ts` | hvigor 构建入口 |
| `hmos/oh-package.json5` | ohpm 依赖声明 |
| `hmos/entry/build-profile.json5` | entry 模块构建配置 |
| `hmos/entry/hvigorfile.ts` | entry 模块 hvigor 入口 |

### 7.4 资源配置

| 文件 | 职责 |
|------|------|
| `hmos/entry/src/main/resources/base/element/string.json` | 字符串资源 |
| `hmos/entry/src/main/resources/base/profile/main_pages.json` | 页面路由 |
| `hmos/entry/src/main/resources/base/profile/backup_config.json` | 备份配置 |

---

## 八、系统要求

| 端 | 要求 |
|----|------|
| 手机 | HarmonyOS NEXT (API 12+ / SDK 6.1+), DevEco Studio |
| PC | Windows 10/11, .NET 8 SDK |
| 可选 | VB-Cable 虚拟音频驱动（手机→PC 虚拟麦克风功能需要） |

---

## 九、常见陷阱速查

| 陷阱 | 正确做法 |
|------|----------|
| hvigor 报 `modelVersion` 未配置 | 确认 `hvigor-config.json5` 在 `hmos/hvigor/` 子目录 |
| NAPI 函数调用报 undefined | 使用 `declare module` + 解构导入，不用 `import * as` + 类型断言 |
| OpusEncoder 报 CS0618 过时 | 改用 `OpusCodecFactory.CreateEncoder()` + `Span<byte>` 重载 |
| TCP 数据解析错乱 | 累积缓冲区 + 按 `payloadLen` 长度驱动解析，不依赖单次 recv 大小 |
| 跨端 timestamp 偏差 | 统一 Int64 LE 格式，ArkTS 端用位运算拆分/重组 |
| 音频缓冲阈值计算错误 | 字节率 192000 B/s = 48k × 2ch × 2B，已含声道和位深，不可重复除以这些系数 |
| 熄屏后音频循环播放旧数据 | writeData 前必须 `fill(0)` 清零整个 buffer |
| dotnet publish 失败 | 先终止同名运行进程 |
| .csproj 不支持 C# 注释 | 注释使用 XML 格式 `<!-- -->`，不能使用 `//` |
| WASAPI EventCallback 无效 | bufferPeriod 和 EventCallback 标志必须同时设置 |
