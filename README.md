# AudioRelayHM

鸿蒙 HarmonyOS NEXT ↔ Windows 双向音频串流

## 项目结构

```
AudioRelayHM/
├── hmos/          — 鸿蒙手机端 App（ArkTS + NAPI C++）
│   └── entry/
│       ├── src/main/ets/
│       │   ├── pages/Index.ets        — 主界面 UI
│       │   ├── model/AudioPacket.ets  — 协议序列化
│       │   └── service/
│       │       ├── AudioCapture.ets   — 麦克风采集
│       │       ├── AudioPlay.ets      — 音频播放
│       │       ├── NetworkService.ets — TCP 网络
│       │       └── OpusDecoderBridge.ets — Opus 解码桥接
│       ├── src/main/cpp/              — Opus 解码（NAPI C++）
│       └── src/main/resources/        — 资源主题配置
│
└── windows/       — Windows PC 端（WinForms + NAudio + Concentus）
    ├── MainForm.cs                    — 主窗体（UI + 网络 + 音频）
    └── AudioRelayWinUI.csproj
```

> 🤖 本项目所有源码均由 AI 辅助生成

## 功能

- **PC → 手机**：Windows 系统音频（WASAPI 环回）→ 手机实时播放
- **手机 → PC**：手机麦克风 → PC 扬声器/虚拟设备实时播放
- **编码方式**：PCM / Opus（32k~192kbps 可选）/ ADPCM
- **缓冲控制**：50ms ~ 1000ms 可调（默认 100ms）
- **实时延迟曲线**：PC 端显示 PC→手机方向的单向网络延迟折线图
- **时钟偏差校准**：NTP 风格 TIME_SYNC 协议，消除双端时钟差
- **输出设备选择**：支持选择 PC 输出设备（含 VB-Cable 等虚拟设备，可作为虚拟麦克风使用）
- **配置热切换**：编码方式、码率、缓冲时间均可运行时调整

## 编译

### 鸿蒙端

使用 DevEco Studio 打开 `hmos/` 目录，连接鸿蒙设备后运行。

### Windows 端

```bash
cd windows
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
```

## 协议

TCP 9287 端口，28 字节头部 + 音频数据负载。

### 控制命令

| 命令 | 值 | 方向 | 说明 |
|------|---|------|------|
| HANDSHAKE | 0 | Phone→PC | 握手请求 |
| HANDSHAKE_ACK | 1 | PC→Phone | 握手响应 |
| HEARTBEAT | 2 | 双向 | 心跳保活（5s） |
| START_STREAM | 3 | 双向 | 开始音频流 |
| STOP_STREAM | 4 | 双向 | 停止音频流 |
| VOLUME | 5 | 双向 | 音量设置 |
| CONFIG | 6 | Phone→PC | 配置参数（编码/码率/缓冲） |
| LATENCY_REPORT | 7 | Phone→PC | 延迟报告（校准后的单向延迟） |
| TIME_SYNC | 8 | 双向 | 时钟同步请求/响应 |

### 时钟同步流程

1. 手机发送 TIME_SYNC，携带手机时间戳 t1
2. PC 收到后记录 t_pc，回传 16 字节（t1 + t_pc）
3. 手机收到回复记录 t3，计算偏差：offset = (t1+t3)/2 - t_pc
4. 后续延迟测量减去 offset，得到真实单向网络延迟

## VB-Cable 虚拟麦克风

将手机音频作为 PC 虚拟麦克风输入（用于会议/直播等场景）：

1. 安装 [VB-Cable](https://vb-audio.com/Cable/)
2. Windows 端输出设备选择 **CABLE Input**
3. 在会议软件中选择 **CABLE Output** 作为麦克风
