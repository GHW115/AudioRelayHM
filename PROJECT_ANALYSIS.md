# AudioRelayHM 项目需求分析与问题清单

> 生成时间：2025-07-15 | 基于全部源代码阅读

---

## 一、项目概述

**AudioRelayHM** 是一款 HarmonyOS NEXT ↔ Windows PC 双向实时音频串流工具。

| 项目 | 值 |
|------|-----|
| 包名 | `com.example.audiorelayhmos` |
| 鸿蒙 SDK | API 6.1.1(24) / compatible 6.1.0(23) |
| Windows 框架 | .NET 8 + WinForms |
| 音频参数 | 48kHz / 2ch / 16bit S16LE (192,000 B/s) |
| 网络 | TCP:9287 (控制) + UDP:9288 (音频) |
| 编码 | PCM(默认) / Opus(32k-192kbps) / ADPCM |
| 缓冲 | 0ms(默认) ~ 1000ms |

---

## 二、功能需求清单

### 2.1 已实现（✅）

| 功能 | PC 端 | 手机端 | 验证状态 |
|------|-------|--------|----------|
| PC→手机 音频串流 | WASAPI Loopback → UDP | NAPI C++ UDP接收 + OHAudio渲染 | ✅ |
| 手机→PC 音频串流 | WaveOutEvent 播放 | AudioCapturer readData → UDP发送 | ✅ |
| PCM 编码 | 直通 float→short→UDP | 环形缓冲直写 | ✅ |
| Opus 编码（PC→手机） | Concentus OpusEncoder | NAPI OH_AudioCodec Opus解码 | ✅ |
| ADPCM 编码（PC→手机） | 自研 ADPCM 编解码器 (C#) | ❌ 未实现 | ⚠️ 单向 |
| 编码/码率/缓冲热切换 | OnConfig 事件处理 | Select 组件 → CONFIG 命令 | ✅ |
| 端到端延迟测量 | 堆叠面积图图表 | ❌ 已禁用 | ❌ 断链 |
| NTP 时钟同步 | TIME_SYNC 回传 | TIME_SYNC 请求 + offset计算 | ✅ |
| VB-Cable 虚拟麦克风 | 输出设备选择器 | — | ✅ |
| 扬声器保护（索引0拦截） | OnOutputDeviceChanged | — | ✅ |
| 熄屏音频保活 | — | AVSession + 后台长时任务 | ✅ |
| 自动重连 | — | 2秒自动重连 | ✅ |
| 端口配置 | 设置页 TextBox | 连接页 TextInput | ✅ |

### 2.2 部分实现（⚠️）

| 功能 | 问题 |
|------|------|
| ADPCM 手机端解码 | C++ 层有 `encoding==2` 分支但注释 "暂不处理"；ArkTS 层 AdpcmDecoder 类存在但未被调用 |
| 延迟分项报告 | PC 端图表支持 4 分项，但手机端 Index.ets 注释 "延迟报告暂禁用，待 NAPI 层添加统计接口后恢复" |
| 编码切换传播 | 手机端 `nativeAudioSetEncoding(0)` 硬编码为 PCM，编码选择器仅影响 PC→手机方向 |
| NAPI 编码切换 | `nativeAudioSetEncoding` 只清空缓冲+同步 epoch，未通知 UDP 接收线程切换解码路径 |

### 2.3 缺失（❌）

| 功能 | 说明 |
|------|------|
| 手机端 ADPCM 解码 | C++ `UdpReceiveLoop` 中 `encoding==2` 分支为空 |
| 手机端 Opus 编码 | 手机→PC 方向始终发送 PCM，不支持 Opus 压缩上传 |
| 延迟分项统计（手机端） | NAPI 层无时间戳记录/回传接口 |
| 连接认证/安全 | 无 TLS、无 token、握手仅字符串比对 "AudioRelayHMOS" |
| 错误恢复 | PC 端无断线重连（仅回到监听状态）；UDP 丢包无 FEC/重传 |
| 音量同步 | VOLUME 命令在协议中定义但两端均未实现 |
| 单元测试 | 双端均无测试文件 |
| 日志持久化 | PC 端日志仅内存 TextBox，无文件输出 |

---

## 三、已知缺陷与风险

### 3.1 严重（🔴）

#### 3.1.1 签名密钥泄露
- **文件**: `hmos/build-profile.json5:4-16`
- **问题**: 包含完整的 `.p12` 证书密码、keyAlias、keyPassword 等签名材料
- **风险**: 任何拿到仓库的人可伪造应用签名
- **建议**: 立即将 `signingConfigs` 移出仓库，使用 `.gitignore` + 环境变量/本地文件注入

#### 3.1.2 PCM↔Opus 编码切换竞态
- **文件**: `windows/MainForm.cs` (AudioCaptureService 类)
- **问题**: `SetEncodingAndBitrate()` 在未持锁情况下替换 `opusEncoder`，`FlushOpus()` 读取 `opusEncoder` 快照后实际使用时可能已被 dispose
- **AGENTS.md 已记录**此已知缺陷，当前 PCM 默认编码下不受影响

#### 3.1.3 全局单例上下文（NAPI C++）
- **文件**: `hmos/entry/src/main/cpp/opus_decoder.cpp`
- **问题**: `g_ctx` 是全局静态指针，`NativeAudioInit` 重复调用返回 -1
- **风险**: 如果 ArkTS 层 start/stop/start 周期中出现异常未调用 stop，再次 start 会失败
- **缓解**: `NativeAudioStop` 中 `delete g_ctx; g_ctx = nullptr` 可以清理

#### 3.1.4 PC 端无 HANDSHAKE/HEARTBEAT 处理
- **文件**: `windows/MainForm.cs` (NetworkServer.StartAsync)
- **问题**: TCP 控制消息循环只处理 CONFIG / LATENCY_REPORT / TIME_SYNC，HANDSHAKE 和 HEARTBEAT 无显式分支
- **后果**: 握手请求被忽略（虽然不影响当前连接建立流程），心跳无响应可能导致手机端误判断线

### 3.2 中等（🟡）

#### 3.2.1 手机→PC 方向始终 PCM
- 手机端编码选择器只影响 PC→手机 方向，手机→PC 方向 `sendAudioData` 始终 `encodingType = EncodingType.PCM`
- 对于移动网络场景，Opus 压缩上传可大幅节省带宽

#### 3.2.2 延迟测量链路断裂
- PC 端图表渲染完整（4分项堆叠面积图），但数据源 `OnLatencyReport` 无手机端生产者
- 手机端 NAPI C++ 层未实现时间戳捕获和延迟报告发送

#### 3.2.3 README.md 严重过时
- 声称包头 28 字节 → 实际 44 字节
- 声称 "TCP ──→ [Opus解码]" → 实际音频走 UDP
- 声称心跳 5s 间隔 → 实际 1s
- 声称 `AudioRenderer.on('writeData')` 拉取模式 → 实际 NAPI C++ OHAudio 回调模式
- 协议表缺少 `encodeTimestamp`、`sendTimestamp` 字段（各8字节 Int64 LE）

#### 3.2.4 OpusEncoder 旧 API
- `windows/MainForm.cs` 使用 `new OpusEncoder()` 直接构造，AGENTS.md 标记应迁移至 `OpusCodecFactory.CreateEncoder()`

#### 3.2.5 Crash dump 文件提交
- `hmos/Crash_175138968.dmp` 和 `hmos/Crash_429809328.dmp` 已提交仓库
- 总计可能数 MB 二进制文件，应清理并加入 `.gitignore`

### 3.3 轻微（🟢）

#### 3.3.1 PC 端 START_STREAM/STOP_STREAM 未实现
- 协议枚举中定义了 `StartStream=3, StopStream=4`，但 PC 端 TCP 处理循环无对应分支

#### 3.3.2 VOLUME 命令未实现
- 协议枚举定义了 `Volume=5`，双端均无实现

#### 3.3.3 手机端 Select 组件缺少 ADPCM 选项
- Index.ets 编码选项为 `['PCM', 'Opus 32k', 'Opus 64k', 'Opus 128k', 'Opus 192k']`，无 ADPCM 选项
- PC 端设置页有 ADPCM 选项

#### 3.3.4 硬编码 9288 UDP 端口
- PC 端 `NetworkServer.UDP_PORT = 9288` 硬编码，手机端同样硬编码
- 用户只能配置 TCP 端口，UDP 端口不可配

---

## 四、文档准确性问题

### 4.1 AGENTS.md vs 实际代码

| AGENTS.md 声称 | 实际代码 | 偏差 |
|----------------|----------|------|
| `OpusCodecFactory.CreateEncoder()` | `new OpusEncoder()` | ⚠️ AGENTS.md 标记为待迁移 |
| `opus_decoder.cpp` 包含 UDP 接收 | 正确 | ✅ |
| 延迟分项 4 个物理分项 | 图表支持但数据源断链 | ❌ |
| HANDSHAKE_ACK(1) 命令 | PC 端无处理 | ❌ |
| PCM 默认编码 | 正确 | ✅ |

### 4.2 README.md vs 实际代码

| README.md 声称 | 实际代码 | 严重性 |
|----------------|----------|--------|
| 包头 28 字节 | 44 字节（含 encodeTimestamp+sendTimestamp 各8字节） | 🔴 |
| PC→手机 音频走 TCP | 走 UDP | 🔴 |
| 心跳 5s 间隔 | 1s 间隔 | 🟡 |
| AudioRenderer.on('writeData') | NAPI OHAudio 回调 | 🟡 |
| 协议表偏移 16-23 为采样率/声道/位深 | 偏移 24-27 为采样率,28-29 为声道/位深（差8字节） | 🔴 |

> **建议**: README.md 应完整重写，或删除并以 AGENTS.md 为单一真相来源。

---

## 五、架构图（基于实际代码）

### 5.1 PC → 手机 方向

```
Windows PC                               HarmonyOS 手机
─────────                               ──────────────
WASAPI Loopback                          NAPI C++ 层
  │ (30ms 缓冲, EventCallback)              │
  ▼                                         │
float→short 转换                            │ UDP socket bind :9288
  │                                         │   │
  ├─ PCM: 重采样至48kHz/2ch → byte[]        │   │ recvfrom()
  ├─ Opus: OpusEncoder.Encode()             │   ▼
  └─ ADPCM: AdpcmCodec.Encode()             │ 解析44字节包头
  │                                         │   │
  ▼                                         │   ├─ encoding==0: PCM → pcmRing.write()
UDP sendto() → phoneEndPoint:9288 ──────────┤   ├─ encoding==1: Opus → inputQueue → OH_AudioCodec解码 → pcmRing
                                            │   └─ encoding==2: ADPCM → ❌ 未实现
                                            │
                                            │ OHAudio OnWriteData 回调
                                            │   │ pcmRing.read()
                                            │   ▼
                                            │ 扬声器播放
```

### 5.2 手机 → PC 方向

```
HarmonyOS 手机                            Windows PC
──────────────                            ──────────
AudioCapturer                              UdpClient :9288
  │ on('readData') 回调                      │ ReceiveAsync()
  │ PCM S16LE                                ▼
  ▼                                        AudioPacket.Deserialize()
NetworkService.sendAudioData()             │
  │ encodingType 始终 = PCM                  ▼
  ▼                                        OnAudioData 事件
UDP sendto() → PC:9288 ──────────────────→ │
                                            ├─ PCM: playback.WriteData(payload)
                                            ├─ ADPCM: AdpcmCodec.Decode() → WriteData
                                            └─ Opus: WriteData(payload) ← ⚠️ PC端无Opus解码!
                                            │
                                            ▼
                                         BufferedWaveProvider → WaveOutEvent
                                           │
                                           ▼
                                         VB-Cable 虚拟麦克风 (DeviceNumber > 0)
                                         或 拒绝播放 (DeviceNumber == 0)
```

### 5.3 控制通道

```
手机 (TCP Client)                         PC (TCP Server :9287)
─────────────────                         ─────────────────────
connect() ─────────────────────────────→ AcceptTcpClientAsync()
  │                                         │
sendHandshake() ────────────────────────→ [被忽略]
  │                                         │
startHeartbeat() 每秒 ─────────────────→ [被忽略]
  │                                         │
sendConfig(enc, bitrate, bufferMs) ────→ OnConfig → 更新编码/缓冲
  │                                         │
sendTimeSync() ────────────────────────→ TIME_SYNC 响应 (t1, t_pc)
  │ ←──── t1 + t_pc ─────────────────── │
  │ clockOffset = (t1+t3)/2 - t_pc        │
```

---

## 六、所需资料 / 待确认事项

### 6.1 产品层面

1. **目标用户场景**：主要是局域网 Wi-Fi 环境，还是需要支持移动网络？
2. **延迟目标**：端到端延迟要求多少 ms？（当前架构理论最低 ~30ms WASAPI + 网络 + OHAudio）
3. **是否需要手机端 ADPCM 支持**？如果不需要，建议移除 ADPCM 选项简化维护
4. **是否需要手机→PC Opus 压缩**？移动网络下可大幅节省上行带宽
5. **是否需要连接认证**？（防止局域网内未授权设备连接）

### 6.2 技术层面

1. **OpusCodecFactory 迁移**：Concentus 库最新版本是否提供了 `CreateEncoder()` 工厂方法？
2. **HarmonyOS NAPI 延迟统计 API**：OHAudio 是否提供 `OH_AudioRenderer_GetLatency()` 或类似接口？
3. **VB-Cable 替代方案**：是否有计划支持 Windows 内置 Stereo Mix 或其他虚拟音频方案？
4. **多客户端支持**：是否需要支持多台手机同时连接一台 PC？

### 6.3 工程层面

1. **CI/CD 流程**：是否有自动化构建/测试流水线？
2. **版本发布策略**：是否需要自动更新机制？
3. **签名证书管理**：是否有安全存储方案替代 `build-profile.json5` 中的硬编码密码？
4. **崩溃日志**：两个 `.dmp` 文件是否有对应的修复记录？

---

## 七、优先修复建议（按紧急程度排序）

| 优先级 | 项目 | 工作量 |
|--------|------|--------|
| 🔴 P0 | 移除 `build-profile.json5` 中的签名密钥 | 0.5h |
| 🔴 P0 | 修复或删除过时的 README.md | 1h |
| 🔴 P0 | 清理 Crash dump 文件并加入 .gitignore | 0.2h |
| 🟡 P1 | 恢复手机端延迟报告（NAPI 添加时间戳统计） | 4-8h |
| 🟡 P1 | PC 端添加 HANDSHAKE 响应和 HEARTBEAT 处理 | 2h |
| 🟡 P1 | 修复 PCM↔Opus 编码切换竞态 | 2h |
| 🟡 P1 | 手机编码选择传播到手机→PC 方向 | 1h |
| 🟢 P2 | OpusEncoder → OpusCodecFactory 迁移 | 1h |
| 🟢 P2 | C++ 层实现 ADPCM 解码 | 3h |
| 🟢 P2 | VOLUME 命令实现 | 2h |
| 🟢 P2 | UDP 端口可配置化 | 1h |
| 🟢 P3 | 添加单元测试 | 8h+ |
| 🟢 P3 | PC 端日志文件持久化 | 1h |
