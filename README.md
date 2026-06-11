# AudioRelayHM

鸿蒙 HarmonyOS NEXT ↔ Windows 双向实时音频串流

## 项目结构

```
AudioRelayHM/
├── hmos/          — 鸿蒙手机端 App（ArkTS + NAPI C++）
│   └── entry/
│       ├── src/main/ets/
│       │   ├── pages/Index.ets        — 主界面 UI + 延迟计算
│       │   ├── model/AudioPacket.ets  — 协议序列化（28字节头 + 负载）
│       │   └── service/
│       │       ├── AudioCapture.ets   — 麦克风采集（手机→PC）
│       │       ├── AudioPlay.ets      — 音频播放（PC→手机，writeData 回调模式）
│       │       ├── NetworkService.ets — TCP 网络（粘包/拆包处理）
│       │       └── OpusDecoderBridge.ets — Opus NAPI 桥接
│       ├── src/main/cpp/              — Opus 解码器（NAPI C++）
│       └── src/main/resources/        — 资源文件
│
└── windows/       — Windows PC 端（WinForms + NAudio + Concentus）
    ├── MainForm.cs                    — 主窗体（UI + 网络 + 音频捕获/播放 + 延迟图表）
    └── AudioRelayWinUI.csproj
```

> 🤖 本项目所有源码均由 AI 辅助生成

## 功能

- **PC → 手机**：WASAPI 环回捕获系统音频 → 实时推送到手机播放
- **手机 → PC**：手机麦克风 → 实时推送到 PC 扬声器/虚拟设备
- **编码方式**：PCM / Opus（32k~192kbps）/ ADPCM
- **缓冲控制**：50ms ~ 1000ms 可配置
- **端到端延迟曲线**：PC 端实时显示延迟折线图（含 WASAPI 采集、编码、网络、解码、缓冲全链路）
- **NTP 时钟同步**：TIME_SYNC 协议消除双端时钟偏差，延迟测量精确到毫秒
- **采样率适配**：PC 端自动将任意采样率/声道重采样到 48kHz 立体声，匹配手机端 AudioRenderer
- **输出设备选择**：支持 VB-Cable 等虚拟设备（可作虚拟麦克风）
- **配置热切换**：编码、码率、缓冲运行时可调
- **后台长时任务**：手机锁屏后音频播放不中断

## 音频架构

### PC → 手机（音频播放）

```
Windows                          手机
WASAPI Loopback ──→ float→short ──→ [Opus编码] ──→ TCP ──→ [Opus解码] ──→ bufferQueue
                                                                      ↓
                                                          AudioRenderer.on('writeData')
                                                              (系统拉取回调)
                                                                      ↓
                                                                 扬声器播放
```

**关键设计**：手机端采用 HarmonyOS `AudioRenderer.on('writeData')` **拉取模式** — 系统按硬件时钟节奏主动请求数据，完美匹配实时速率，无需 setTimeout 定时写入，队列长期稳定。

### 手机 → PC（麦克风）

```
手机麦克风 ──→ PCM ──→ TCP ──→ PC BufferedWaveProvider ──→ WaveOutEvent 播放
```

## 编译

### 鸿蒙端

使用 DevEco Studio 打开 `hmos/` 目录，连接鸿蒙设备后直接运行。

### Windows 端

需要 .NET 8 SDK：

```bash
cd windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

生成文件位于 `windows/bin/Release/net8.0-windows/win-x64/publish/AudioRelayWinUI.exe`

## 协议

TCP 9287 端口，28 字节头部 + 音频负载。

### 包格式

| 偏移 | 长度 | 类型 | 说明 |
|------|------|------|------|
| 0 | 1 | uint8 | 消息类型（0=控制, 1=音频） |
| 1 | 1 | uint8 | 控制命令 / 0xFF |
| 2 | 1 | uint8 | 流方向（0=PC→手机, 1=手机→PC）/ 0xFF |
| 3 | 1 | uint8 | 编码类型（0=PCM, 1=Opus, 2=ADPCM） |
| 4 | 4 | int32 LE | 序列号 |
| 8 | 8 | int64 LE | 时间戳（毫秒） |
| 16 | 4 | int32 LE | 采样率 |
| 20 | 1 | uint8 | 声道数 |
| 21 | 1 | uint8 | 位深 |
| 22 | 2 | uint16 | 保留 |
| 24 | 4 | int32 LE | 负载长度 |
| 28+ | N | bytes | 负载数据 |

### 控制命令

| 命令 | 值 | 方向 | 说明 |
|------|---|------|------|
| HANDSHAKE | 0 | Phone→PC | 握手请求 |
| HANDSHAKE_ACK | 1 | PC→Phone | 握手响应 |
| HEARTBEAT | 2 | 双向 | 心跳（5s 间隔） |
| START_STREAM | 3 | 双向 | 开始音频流 |
| STOP_STREAM | 4 | 双向 | 停止音频流 |
| VOLUME | 5 | 双向 | 音量设置 |
| CONFIG | 6 | Phone→PC | 配置参数（编码/码率/缓冲） |
| LATENCY_REPORT | 7 | Phone→PC | 端到端延迟报告 |
| TIME_SYNC | 8 | 双向 | 时钟同步请求/响应 |

### 时钟同步

NTP 风格校准，消除双端时钟偏差：

1. 手机发送 `TIME_SYNC`，携带手机时间戳 t1
2. PC 记录本地时间 t_pc，回传 (t1, t_pc)
3. 手机记录收到时间 t3，计算偏差：`offset = (t1 + t3) / 2 - t_pc`
4. 延迟测量时减去 offset，得到真实端到端延迟

## VB-Cable 虚拟麦克风

将手机麦克风作为 PC 虚拟麦克风（适用于会议/直播场景）：

1. 安装 [VB-Cable](https://vb-audio.com/Cable/)
2. PC 端输出设备选择 **CABLE Input**
3. 会议软件中选择 **CABLE Output** 作为麦克风输入
