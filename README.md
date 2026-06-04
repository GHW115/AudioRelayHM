# AudioRelayHM

鸿蒙 HarmonyOS NEXT ↔ Windows 双向音频串流

## 项目结构

```
AudioRelayHM/
├── hmos/          — 鸿蒙手机端 App（ArkTS + NAPI C++）
│   └── entry/
│       ├── src/main/ets/     — UI + 服务逻辑
│       ├── src/main/cpp/     — Opus 解码（NAPI）
│       └── src/main/resources/ — 资源主题配置
│
└── windows/       — Windows PC 端（WinForms + NAudio + Concentus）
    ├── MainForm.cs
    └── AudioRelayWinUI.csproj
```

## 功能

- **PC → 手机**：Windows 系统音频 → 手机实时播放
- **手机 → PC**：手机麦克风 → PC 实时播放
- 编码方式：PCM / Opus（32k~192kbps 可选）
- 可调节缓冲时间（200ms~5000ms）

## 编译

### HMOS 端
使用 DevEco Studio 打开 `hmos/` 目录，连接鸿蒙设备后运行。

### Windows 端
```bash
cd windows
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
```

## 协议

TCP 9287 端口，28 字节头部 + 音频数据负载。
