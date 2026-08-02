# AudioRelayHM

鸿蒙 HarmonyOS NEXT ↔ Windows 双向实时音频串流

> 🤖 本项目所有源码均由 AI（DeepSeek V4）辅助生成

## 项目结构

```
AudioRelayHM/
├── hmos/                     — 鸿蒙手机端（ArkTS + NAPI C++）
│   ├── build-profile.json5   — 签名配置（${env.OHOS_*} 环境变量引用，密钥不入库）
│   ├── signing.local.env     — 本地签名材料（gitignored，含证书密码）
│   └── entry/src/main/
│       ├── ets/
│       │   ├── pages/Index.ets       — 主界面 UI（连接/参数/延迟）
│       │   ├── model/AudioPacket.ets — 协议序列化（44 字节包头）
│       │   └── service/
│       │       ├── AudioCapture.ets   — 麦克风采集（手机→PC）
│       │       ├── AudioPlay.ets      — NAPI 管线控制 + AVSession + 后台任务
│       │       ├── AdpcmDecoder.ets   — IMA ADPCM 解码器（测试页使用）
│       │       ├── NetworkService.ets — TCP 控制 + UDP 发送 + 粘包处理
│       │       └── OpusDecoderBridge.ets — NAPI 模块类型声明
│       └── cpp/opus_decoder.cpp  — NAPI C++：UDP接收 + Opus解码 + OHAudio渲染
│
├── windows/                  — Windows PC 端（WinForms + NAudio + Concentus）
│   ├── MainForm.cs           — 主窗体（布局 + 服务编排）
│   ├── NetworkServer.cs      — TCP 控制 + UDP 音频收发
│   ├── AudioServices.cs      — WASAPI 环回捕获 + WaveOut 播放
│   ├── AdpcmCodec.cs         — IMA ADPCM 编解码器
│   ├── Protocol.cs           — 网络包协议（44 字节包头）
│   ├── UI/Controls.cs        — 自绘控件（主题/圆角卡片/导航/延迟图表）
│   └── AudioRelayWinUI.Tests/ — 单元测试（xUnit, 37 cases）
│
└── scripts/
    ├── build-hap.ps1         — 鸿蒙命令行构建（自动注入本地签名并恢复安全配置）
    └── apply-signing-env.ps1 — 将本地签名导入用户环境变量（DevEco GUI 用）
```

## 功能

- **PC → 手机**：WASAPI 环回捕获系统音频 → UDP 实时推送到手机播放
- **手机 → PC**：手机麦克风 → UDP 实时推送到 PC 虚拟设备
- **编码方式**：PCM（默认，零编解码延迟）/ Opus（32k~192kbps）
- **缓冲控制**：0ms ~ 1000ms 可配置（手机端 UI 控制）
- **端到端延迟曲线**：PC 端堆叠面积图（4 分项：PC处理/网络/缓冲/渲染），NAPI 层 200ms 采样上报
- **NTP 时钟同步**：TIME_SYNC 协议消除双端时钟偏差
- **采样率适配**：PC 端自动将任意采样率/声道重采样到 48kHz 立体声
- **输出设备选择**：支持 VB-Cable 等虚拟设备（可作虚拟麦克风）
- **扬声器保护**：默认设备（索引0）被选中时强制停止手机→PC 播放
- **配置热切换**：编码、码率、缓冲运行时可调（手机端为主控，PC 端设置页为本地默认值）
- **后台长时任务**：手机锁屏后音频播放不中断（AVSession + backgroundModes）
- **自动重连**：手机端断线后 2 秒自动重连

## 网络架构

| 协议 | 端口 | 用途 |
|------|------|------|
| **TCP** | 9287（可配置） | 控制消息：握手/配置/时钟同步/心跳/延迟报告 |
| **UDP** | 9288（固定） | 双向音频数据传输 |

PC 端作为 TCP Server 监听，手机端主动连接。音频数据走 UDP 以减少延迟。

## 音频架构

### PC → 手机

```
Windows                                     HarmonyOS 手机
WASAPI Loopback (30ms缓冲, EventCallback)    NAPI C++ 层
  │ float→short 转换                            │ UDP socket bind :9288
  ├─ PCM: 重采样至48kHz/2ch → 发送              │ recvfrom() → 解析44字节包头
  ├─ Opus: OpusCodecFactory → 发送              │   ├─ PCM: → pcmRing.write()
  └─ ADPCM: AdpcmCodec.Encode → 发送            │   └─ Opus: → OH_AudioCodec解码 → pcmRing
  │                                             │
  ▼ UDP sendto() :9288 ─────────────────────→   │ OHAudio OnWriteData 回调(pcmRing.read)
                                                ▼
                                              扬声器播放
```

手机端 PC→手机 方向已完全下沉至 **NAPI C++ 原生管线**（UDP接收 + Opus解码 + OHAudio渲染），绕过 JS 事件循环。

### 手机 → PC

```
HarmonyOS 手机                              Windows PC
AudioCapturer.on('readData') 回调              UdpClient :9288
  │ PCM S16LE                                    │ ReceiveAsync()
  ▼                                               ▼
UDP sendto() → PC:9288 ────────────────────→   OnAudioData 事件
                                                  │
                                                  ├─ PCM: BufferedWaveProvider.Write()
                                                  ├─ ADPCM: AdpcmCodec.Decode() → Write()
                                                  ▼
                                                WaveOutEvent → VB-Cable 虚拟麦克风
```

## 音频参数

| 项目 | 值 |
|------|-----|
| 采样率 | 48kHz |
| 声道 | 2（立体声） |
| 位深 | 16bit S16LE |
| 原始码率 | 192,000 B/s |
| 默认编码 | PCM（零编解码延迟） |
| 默认缓冲 | 0ms（无缓冲模式） |

## 协议

TCP 9287 + UDP 9288，**44 字节**包头 + 音频负载。

### 包格式

| 偏移 | 长度 | 类型 | 说明 |
|------|------|------|------|
| 0 | 1 | uint8 | 消息类型（0=控制, 1=音频） |
| 1 | 1 | uint8 | 控制命令 / 0xFF（未设置） |
| 2 | 1 | uint8 | 流方向 / 0xFF（未设置） |
| 3 | 1 | uint8 | 编码类型（0=PCM, 1=Opus, 2=ADPCM） |
| 4 | 4 | int32 LE | 序列号 |
| 8 | 8 | int64 LE | 采集时间戳（毫秒） |
| 16 | 8 | int64 LE | 编码完成时间戳（毫秒） |
| 24 | 8 | int64 LE | 发送时间戳（毫秒） |
| 32 | 4 | int32 LE | 采样率 |
| 36 | 1 | uint8 | 声道数 |
| 37 | 1 | uint8 | 位深 |
| 38 | 2 | uint16 | 保留 |
| 40 | 4 | int32 LE | 负载长度 |
| 44+ | N | bytes | 负载数据 |

### 控制命令

| 命令 | 值 | 方向 | 说明 |
|------|---|------|------|
| HANDSHAKE | 0 | Phone→PC | 握手请求 (payload: "AudioRelayHMOS") |
| HANDSHAKE_ACK | 1 | PC→Phone | 握手响应 |
| HEARTBEAT | 2 | 双向 | 心跳保活（1s 间隔） |
| START_STREAM | 3 | 双向 | 开始音频流（枚举保留） |
| STOP_STREAM | 4 | 双向 | 停止音频流（枚举保留） |
| VOLUME | 5 | 双向 | 音量设置（暂未实现） |
| CONFIG | 6 | Phone→PC | 配置下发（9字节：encoding+bitrate+bufferMs） |
| LATENCY_REPORT | 7 | Phone→PC | 延迟分项报告（16字节 4×int32 LE） |
| TIME_SYNC | 8 | 双向 | 时钟同步（NTP风格） |

### 时钟同步

NTP 风格校准，消除双端时钟偏差：

1. 手机发送 `TIME_SYNC`，携带手机时间戳 t1
2. PC 记录本地时间 t_pc，回传 (t1, t_pc)
3. 手机记录收到时间 t3，计算偏差：`offset = (t1 + t3) / 2 - t_pc`
4. 延迟测量时减去 offset，得到真实端到端延迟

## 编译

### 鸿蒙端

签名材料**不入库**：`build-profile.json5` 通过 `${env.OHOS_*}` 引用环境变量，真实值存于 gitignored 的 `hmos/signing.local.env`。

**方式一：DevEco Studio GUI**

> ⚠️ 本 SDK 的 hvigor 不解析 `${env.X}` 语法（GUI/命令行均如此，报错 00303107）。仓库中的 `build-profile.json5` 是环境变量引用版（安全），GUI 构建前需生成本地明文版：

1. 首次使用（或换证书后）运行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/setup-local-signing.ps1
   ```
   该脚本从 `hmos/signing.local.env` 生成本地明文 `build-profile.json5`，并自动 `git update-index --skip-worktree`（明文永远不会被提交）
2. 重启或刷新 DevEco Studio
3. 打开 `hmos/` 目录，连接鸿蒙设备后 Run

**方式二：命令行构建（推荐 CI/脚本场景）**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-hap.ps1 -BuildMode debug
```

脚本自动：读取本地签名 → 生成临时明文配置 → 调用 hvigorw 构建 → 恢复配置。
产物：`hmos/entry/build/default/outputs/default/entry-default-signed.hap`

> ⚠️ 不要直接运行 hvigorw：命令行 hvigor 不解析 `${env.X}`，且 JSON 单反斜杠会被解析器吞掉。

### Windows 端

需要 .NET 8 SDK：

```bash
cd windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

生成文件位于 `windows/bin/Release/net8.0-windows/win-x64/publish/AudioRelayWinUI.exe`（单文件，免安装 .NET 运行时）。

## 测试

```bash
# Windows 端（37 个单元测试：协议往返/编解码/网络输入防御）
dotnet test windows/AudioRelayWinUI.Tests/

# 鸿蒙端
# DevEco Studio 中右键 ohosTest → Run 'ohosTest'
```

每次 `git commit` 自动运行 Windows 端测试（pre-commit hook）。

## VB-Cable 虚拟麦克风

将手机麦克风作为 PC 虚拟麦克风（适用于会议/直播场景）：

1. 安装 [VB-Cable](https://vb-audio.com/Cable/)
2. PC 端设置页输出设备选择 **CABLE Input**
3. 会议软件中选择 **CABLE Output** 作为麦克风输入

> 安全规则：手机→PC 音频**绝不会**输出到物理扬声器；选中默认设备（索引0）时播放会被强制停止。

## 已知限制

- 手机→PC 方向始终使用 PCM 编码，不支持 Opus 压缩上传（移动网络下带宽较高）
- 手机端 ADPCM 解码未实现（PC 端编码器选择 ADPCM 时手机无法播放）
- UDP 端口 9288 固定不可配置（仅 TCP 端口可改）
- 无连接认证（局域网内任何设备可连接，无 TLS/token）
- VOLUME 命令两端均未实现
