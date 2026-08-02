// 🤖 AI 辅助生成 — DeepSeek V4
// 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
// 全 NAPI 原生音频管线：UDP接收 → Opus解码 → OHAudio渲染（绕过JS事件循环）

#include "napi/native_api.h"
#include "multimedia/player_framework/native_avcodec_audiocodec.h"
#include "multimedia/player_framework/native_avcodec_base.h"
#include "multimedia/player_framework/native_avformat.h"
#include "multimedia/player_framework/native_avbuffer.h"
#include "ohaudio/native_audiorenderer.h"
#include "ohaudio/native_audiostreambuilder.h"
#include "ohaudio/native_audiostream_base.h"
#include <cstring>
#include <algorithm>
#include <mutex>
#include <condition_variable>
#include <vector>
#include <thread>
#include <queue>
#include <atomic>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <fcntl.h> // O_NONBLOCK (drainSocket)
#include <sys/time.h>
#include <dlfcn.h> // dlopen/dlsym 动态加载 OHAudio 新版 API
#include <qos/qos.h> // OH_QoS_SetThreadQoS 音频回调线程优先级

#define MAX_PACKET_SIZE 65536
#define HEADER_SIZE 44
#define MAX_QUEUED_OPUS_FRAMES 100 // Opus 解码队列上限（~2s @ 20ms/帧），防止网络输入无界堆积
#define RING_BUFFER_CAPACITY (1024 * 1024) // 1MB ring buffer (~5.4s @ 48kHz/2ch/16bit)
#define MAX_BUFFER_MS 100  // 环形缓冲硬上限(ms)，超过则丢帧防止延迟无限堆积

// ======================== 环形音频缓冲区 ========================
// 原为无锁 SPSC 设计（Writer 仅改 writePos_，Reader 仅改 readPos_），
// 但 clear()/dropUntil() 会被第三方线程（NAPI 线程、中断回调、UDP 线程）调用，
// 破坏"单写者/单读者"不变量。现改为互斥锁保护，保证任意调用点并发安全。
// 调用频率为每 10ms 音频回调/每网络包一次，锁开销可忽略。
class AudioRingBuffer {
public:
    AudioRingBuffer(int capacity = RING_BUFFER_CAPACITY)
        : buf_(capacity, 0), capacity_(capacity), readPos_(0), writePos_(0),
          underrunEpoch_(0) {}

    // Writer side: block-copy write. Returns bytes actually written (may drop tail if full)
    int write(const uint8_t *data, int size) {
        std::lock_guard<std::mutex> lock(mtx_);
        int readPos  = readPos_.load(std::memory_order_acquire);
        int writePos = writePos_.load(std::memory_order_relaxed);

        // 计算可用空间（保留 1 个 slot 区分 empty vs full）
        int used;
        if (writePos >= readPos) used = writePos - readPos;
        else                     used = capacity_ - readPos + writePos;
        int free = capacity_ - used - 1;

        int toWrite = (size < free) ? size : free;
        if (toWrite <= 0) return 0;

        int firstPart = capacity_ - writePos;
        if (firstPart > toWrite) firstPart = toWrite;
        memcpy(&buf_[writePos], data, firstPart);
        if (firstPart < toWrite) {
            memcpy(&buf_[0], data + firstPart, toWrite - firstPart);
        }

        writePos_.store((writePos + toWrite) % capacity_, std::memory_order_release);
        return toWrite;
    }

    // Reader side: block-copy read. 不足部分填零并记录 underrun
    int read(uint8_t *data, int size) {
        std::lock_guard<std::mutex> lock(mtx_);
        int readPos  = readPos_.load(std::memory_order_relaxed);
        int writePos = writePos_.load(std::memory_order_acquire);

        int available;
        if (writePos >= readPos) available = writePos - readPos;
        else                     available = capacity_ - readPos + writePos;

        int toRead = (size < available) ? size : available;
        int firstPart = capacity_ - readPos;
        if (firstPart > toRead) firstPart = toRead;

        if (toRead > 0) {
            memcpy(data, &buf_[readPos], firstPart);
            if (firstPart < toRead) {
                memcpy(data + firstPart, &buf_[0], toRead - firstPart);
            }
        }

        readPos_.store((readPos + toRead) % capacity_, std::memory_order_release);

        // Underrun: buffer 不足以填满请求→补齐静音
        if (toRead < size) {
            memset(data + toRead, 0, size - toRead);
            underrunEpoch_.fetch_add(1, std::memory_order_acq_rel);
        }

        return toRead;
    }

    void clear() {
        // 带锁：任何线程、任何时刻（包括 writer/reader 活跃时）均可安全调用
        std::lock_guard<std::mutex> lock(mtx_);
        writePos_.store(0, std::memory_order_release);
        readPos_.store(0, std::memory_order_release);
    }

    int available() const {
        std::lock_guard<std::mutex> lock(mtx_);
        int readPos  = readPos_.load(std::memory_order_acquire);
        int writePos = writePos_.load(std::memory_order_acquire);
        if (writePos >= readPos) return writePos - readPos;
        return capacity_ - readPos + writePos;
    }

    uint32_t getUnderrunEpoch() const {
        std::lock_guard<std::mutex> lock(mtx_);
        return underrunEpoch_.load(std::memory_order_acquire);
    }

    // 供 reader 侧延迟守卫使用：直接跳过陈旧数据（在 OnAudioWriteData 中调用）
    int dropUntil(int targetAvailable) {
        std::lock_guard<std::mutex> lock(mtx_);
        int readPos  = readPos_.load(std::memory_order_relaxed);
        int writePos = writePos_.load(std::memory_order_acquire);

        int available;
        if (writePos >= readPos) available = writePos - readPos;
        else                     available = capacity_ - readPos + writePos;

        if (available <= targetAvailable) return 0;

        int toDrop = available - targetAvailable;
        readPos_.store((readPos + toDrop) % capacity_, std::memory_order_release);
        return toDrop;
    }

private:
    std::vector<uint8_t> buf_;
    int capacity_;
    mutable std::mutex mtx_; // 保护所有指针操作（多生产者/多消费者 + clear）
    std::atomic<int> readPos_;
    std::atomic<int> writePos_;
    std::atomic<uint32_t> underrunEpoch_;
};

// ======================== OHAudio 动态 API 加载 ========================
// 参考 moonlight-harmony audio_renderer.cpp 的 dlsym 模式
// API 12+ 新增的函数通过 dlopen/dlsym 动态加载，兼容旧设备
typedef OH_AudioStream_Result (*PFN_SetRendererInterruptCb)(
    OH_AudioStreamBuilder*, OH_AudioRenderer_OnInterruptCallback, void*);
typedef OH_AudioStream_Result (*PFN_SetRendererErrorCb)(
    OH_AudioStreamBuilder*, OH_AudioRenderer_OnErrorCallback, void*);
typedef OH_AudioStream_Result (*PFN_SetRendererDeviceChangeCb)(
    OH_AudioStreamBuilder*, OH_AudioRenderer_OutputDeviceChangeCallback, void*);

static PFN_SetRendererInterruptCb    g_pfnSetRendererInterruptCb = nullptr;
static PFN_SetRendererErrorCb        g_pfnSetRendererErrorCb     = nullptr;
static PFN_SetRendererDeviceChangeCb g_pfnSetRendererDeviceChangeCb = nullptr;
static bool g_audioApisChecked = false;

static void LoadAudioApis() {
    if (g_audioApisChecked) return;
    g_audioApisChecked = true;

    void *handle = dlopen("libohaudio.so", RTLD_NOW);
    if (!handle) return;

    g_pfnSetRendererInterruptCb = (PFN_SetRendererInterruptCb)
        dlsym(handle, "OH_AudioStreamBuilder_SetRendererInterruptCallback");
    g_pfnSetRendererErrorCb = (PFN_SetRendererErrorCb)
        dlsym(handle, "OH_AudioStreamBuilder_SetRendererErrorCallback");
    g_pfnSetRendererDeviceChangeCb = (PFN_SetRendererDeviceChangeCb)
        dlsym(handle, "OH_AudioStreamBuilder_SetRendererOutputDeviceChangeCallback");
    // 注意：不调用 dlclose，保持 so 加载直到进程退出
}

// ======================== 全局音频管线上下文 ========================
struct NativeAudioContext {
    // UDP 接收
    int udpSocket = -1;
    std::thread *udpThread = nullptr;
    std::atomic<bool> running{false};
    int udpPort = 9288;
    char pcAddress[64] = {0}; // PC 的 IP 地址（从首次收到 UDP 包时记录）

    // 编码类型：0=PCM, 1=Opus, 2=ADPCM
    std::atomic<int> encodingType{0};

    // Opus 解码
    OH_AVCodec *codec = nullptr;
    std::thread *decodeThread = nullptr;
    std::atomic<bool> decodeActive{false}; // 解码器生命周期标志（编码切换/停止时唤醒线程退出）
    std::mutex codecMtx;
    std::condition_variable cvInput;
    bool inputReady = false;
    uint32_t inputIndex = 0;
    OH_AVBuffer *inputBuffer = nullptr;
    int pendingInputs = 0;
    std::mutex inputMtx;
    std::condition_variable inputCv;
    std::queue<std::vector<uint8_t>> inputQueue;

    // PCM 环形缓冲区（解码后的 PCM 或直通 PCM 都写入这里）
    AudioRingBuffer pcmRing;

    // 写入端同步 epoch（与 pcmRing.underrunEpoch 比较，检测是否需要恢复）
    // 采用阈值方案：gap > 200 空读（约2秒）才触发恢复，避免网络微抖动误触发
    std::atomic<uint32_t> writeEpoch{0};

    // OHAudio 渲染器
    OH_AudioRenderer *renderer = nullptr;
    int sampleRate = 48000;
    int channels = 2;
    int rendererLatencyMs = 0; // OHAudio硬件延迟（启动时获取，每5s更新）
    int64_t lastLatencyQueryTime = 0; // 上次查询渲染延迟的时间戳
    std::atomic<int> maxBufferMs{100}; // 环形缓冲上限(ms)，由ArkTS端设置，默认100ms

    // 时钟偏移（手机时间 - PC时间，由TIME_SYNC协议校准，单位ms）
    std::atomic<int64_t> clockOffset{0};

    // 最近一帧的延迟分项统计（由UDP线程写入，NAPI读取）
    std::atomic<int> latestNetworkMs{0};   // 网络传输延迟
    std::atomic<int> latestPcProcessMs{0}; // PC端处理延迟(encodeTime - captureTime)
    std::atomic<int> latestPcmBufferMs{0}; // PCM环形缓冲排队延迟
    std::atomic<int64_t> latencyUpdateTime{0}; // 上次更新统计的时间戳(ms)

    // Underrun 淡入淡出状态（OHAudio 回调线程独占，无需原子）
    int fadeState = 0;  // 0=正常, 1=underrun中(需要淡出), 2=恢复中(需要淡入)
    int fadeFrameCount = 0; // 当前 fade 已处理帧数
};

static NativeAudioContext *g_ctx = nullptr;

// ======================== Opus 解码回调 ========================
static void OnCodecError(OH_AVCodec *, int32_t, void *) {}
static void OnCodecStreamChanged(OH_AVCodec *, OH_AVFormat *, void *) {}

static void OnCodecNeedInput(OH_AVCodec *, uint32_t index, OH_AVBuffer *buffer, void *userData) {
    auto *ctx = (NativeAudioContext *)userData;
    std::lock_guard<std::mutex> lock(ctx->codecMtx);
    ctx->inputIndex = index;
    ctx->inputBuffer = buffer;
    ctx->inputReady = true;
    ctx->cvInput.notify_one();
}

static void OnCodecNewOutput(OH_AVCodec *codec, uint32_t index, OH_AVBuffer *buffer, void *userData) {
    auto *ctx = (NativeAudioContext *)userData;
    OH_AVCodecBufferAttr attr;
    OH_AVBuffer_GetBufferAttr(buffer, &attr);
    if (attr.size > 0 && !(attr.flags & AVCODEC_BUFFER_FLAGS_EOS)) {
        uint8_t *data = OH_AVBuffer_GetAddr(buffer);
        if (data) {
            ctx->pcmRing.write(data + attr.offset, attr.size);
        }
    }
    {
        std::lock_guard<std::mutex> lock(ctx->codecMtx);
        if (ctx->pendingInputs > 0) ctx->pendingInputs--;
    }
    OH_AudioCodec_FreeOutputBuffer(codec, index);
}

// ======================== Opus 解码线程 ========================
static void OpusDecodeLoop(NativeAudioContext *ctx) {
    while (ctx->running.load() && ctx->decodeActive.load()) {
        std::vector<uint8_t> input;
        {
            std::unique_lock<std::mutex> lock(ctx->inputMtx);
            ctx->inputCv.wait_for(lock, std::chrono::milliseconds(50),
                [ctx] { return !ctx->inputQueue.empty() || !ctx->running.load() || !ctx->decodeActive.load(); });
            if (!ctx->running.load() || !ctx->decodeActive.load()) break;
            if (ctx->inputQueue.empty()) continue;
            input = std::move(ctx->inputQueue.front());
            ctx->inputQueue.pop();
        }

        // Feed input to codec
        {
            std::unique_lock<std::mutex> lock(ctx->codecMtx);
            if (!ctx->cvInput.wait_for(lock, std::chrono::milliseconds(200),
                [ctx] { return ctx->inputReady || !ctx->running.load() || !ctx->decodeActive.load(); })) {
                continue;
            }
            if (!ctx->running.load() || !ctx->decodeActive.load()) break;
            ctx->inputReady = false;
        }

        if (ctx->inputBuffer && input.size() <= MAX_PACKET_SIZE) {
            uint8_t *buf = OH_AVBuffer_GetAddr(ctx->inputBuffer);
            if (buf) memcpy(buf, input.data(), input.size());
            OH_AVCodecBufferAttr attr;
            attr.size = (int32_t)input.size();
            attr.offset = 0;
            attr.pts = 0;
            attr.flags = AVCODEC_BUFFER_FLAGS_NONE;
            OH_AVBuffer_SetBufferAttr(ctx->inputBuffer, &attr);
            {
                std::lock_guard<std::mutex> lock(ctx->codecMtx);
                ctx->pendingInputs++;
            }
            OH_AudioCodec_PushInputBuffer(ctx->codec, ctx->inputIndex);
        }
    }
}

// ======================== OHAudio 中断回调 ========================
// B站等视频App播放时主动让出音频焦点，对方停止后自动恢复
static void OnAudioInterrupt(OH_AudioRenderer *renderer, void *userData,
                              OH_AudioInterrupt_ForceType /*type*/, OH_AudioInterrupt_Hint hint) {
    auto *ctx = (NativeAudioContext *)userData;
    if (!ctx || !ctx->running.load()) return;

    if (hint == AUDIOSTREAM_INTERRUPT_HINT_RESUME) {
        // B站停了，系统通知可以恢复
        // 清空堆积的过期数据，避免恢复后延迟爆炸
        ctx->pcmRing.clear();
        {
            std::lock_guard<std::mutex> lock(ctx->inputMtx);
            while (!ctx->inputQueue.empty()) ctx->inputQueue.pop();
        }
        ctx->writeEpoch.store(ctx->pcmRing.getUnderrunEpoch(), std::memory_order_release);
        // 重启 OHAudio 渲染器
        OH_AudioRenderer_Start(renderer);
    }
    // PAUSE/STOP: 不做任何事，让出音频焦点给B站
}

// ======================== OHAudio 错误回调 ========================
// 参考 moonlight audio_renderer.cpp: 连续错误超过阈值时标记 needRestart
static void OnAudioError(OH_AudioRenderer * /*renderer*/, void *userData,
                          OH_AudioStream_Result error) {
    auto *ctx = (NativeAudioContext *)userData;
    // 仅记录错误，真正的重启逻辑由 UDP 线程的定期检测触发
    // （与 moonlight 不同：我们不在此处立即重启，避免音频回调线程阻塞）
    (void)ctx;
    (void)error;
}

// ======================== OHAudio 设备变更回调 ========================
// 跟踪扬声器→蓝牙→耳机的切换，配合定期 GetLatency 更新渲染延迟
static void OnAudioDeviceChange(OH_AudioRenderer * /*renderer*/, void * /*userData*/,
                                 OH_AudioStream_DeviceChangeReason /*reason*/) {
    // 设备变更时让定期延迟查询刷新 rendererLatencyMs（在 UDP 线程中处理）
}

// ======================== 淡入淡出常量 ========================
// 参考 moonlight-harmony audio_renderer.cpp 的 fade 设计
// 防止 underrun/overrun 时产生可听咔嗒声（pop/click）
#define FADE_FRAMES_MIN 96   // 最小 fade 帧数 (2ms @ 48kHz)
#define FADE_FRAMES_MAX 480  // 最大 fade 帧数 (10ms @ 48kHz)
// 延迟守卫：缓冲超过此阈值时自动丢弃陈旧帧（参考 moonlight MAX_AUDIO_LATENCY_MS）
// 此值会与用户配置的 maxBufferMs 取较大者
#define LATENCY_GUARD_MS 200

// ======================== OHAudio OnWriteData 回调（系统音频线程） ========================
static OH_AudioData_Callback_Result OnAudioWriteData(
    OH_AudioRenderer * /*renderer*/, void *userData,
    void *audioData, int32_t audioDataSize)
{
    auto *ctx = (NativeAudioContext *)userData;

    // ======== QoS: 将音频回调线程设置为最高交互优先级（仅设置一次） ========
    // 参考 moonlight-harmony audio_renderer.cpp
    static thread_local bool qosSet = false;
    if (!qosSet) {
        int ret = OH_QoS_SetThreadQoS(QOS_USER_INTERACTIVE);
        if (ret == 0) {
            qosSet = true;
        }
    }
    // =====================================================================

    int sampleCount = audioDataSize / (int)sizeof(int16_t);
    int channelCount = std::max(ctx->channels, 1);

    // ======== 延迟守卫：缓冲堆积超过阈值，丢弃陈旧数据 ========
    int bufferedMs = ctx->pcmRing.available() / 192; // bytes / (48000*2*2/1000)
    int capMs = ctx->maxBufferMs.load(std::memory_order_acquire);
    if (capMs <= 0) capMs = LATENCY_GUARD_MS;
    int guardMs = (capMs > LATENCY_GUARD_MS) ? capMs : LATENCY_GUARD_MS;

    if (bufferedMs > guardMs) {
        // 保留 guardMs/2 的数据，丢弃多余的陈旧帧（对齐声道边界）
        int targetBytes = (guardMs / 2) * 192;
        targetBytes = (targetBytes / channelCount) * channelCount; // 对齐声道
        ctx->pcmRing.dropUntil(targetBytes);
    }
    // ============================================================

    // ======== 从环形缓冲读取 PCM 数据 ========
    int bytesRead = ctx->pcmRing.read((uint8_t *)audioData, audioDataSize);
    int16_t *outBuf = (int16_t *)audioData;
    int framesRead = bytesRead / (int)sizeof(int16_t) / channelCount;
    int framesNeeded = sampleCount / channelCount;
    // ==========================================

    // ======== Fade-Out：数据不足时，对尾部有效数据做淡出再补零 ========
    if (bytesRead < audioDataSize && framesRead > 0) {
        int gap = framesNeeded - framesRead;
        int fadeFrames = FADE_FRAMES_MIN;
        if (gap > 0) {
            float gapRatio = (float)gap / (float)framesNeeded;
            fadeFrames = FADE_FRAMES_MIN + (int)((FADE_FRAMES_MAX - FADE_FRAMES_MIN) * gapRatio);
        }
        if (fadeFrames > framesRead) fadeFrames = framesRead;

        int fadeStart = (framesRead - fadeFrames) * channelCount;
        for (int f = 0; f < fadeFrames; f++) {
            float gain = 1.0f - (float)f / (float)fadeFrames;
            for (int c = 0; c < channelCount; c++) {
                int idx = fadeStart + f * channelCount + c;
                outBuf[idx] = (int16_t)(outBuf[idx] * gain);
            }
        }
        // 尾部补零
        memset(outBuf + framesRead * channelCount, 0,
               (framesNeeded - framesRead) * channelCount * sizeof(int16_t));
        ctx->fadeState = 1; // 标记进入 underrun
        ctx->fadeFrameCount = 0;
    }
    // =============================================================

    // ======== Fade-In：从 underrun 恢复时，对首段数据做淡入 ========
    else if (framesRead > 0 && ctx->fadeState == 1) {
        int fadeFrames = (framesRead < FADE_FRAMES_MIN) ? framesRead : FADE_FRAMES_MIN;
        for (int f = 0; f < fadeFrames; f++) {
            float gain = (float)f / (float)fadeFrames;
            for (int c = 0; c < channelCount; c++) {
                outBuf[f * channelCount + c] = (int16_t)(outBuf[f * channelCount + c] * gain);
            }
        }
        ctx->fadeState = 0; // 恢复正常
        ctx->fadeFrameCount = 0;
    }
    // =============================================================

    // ======== 完全 underrun（无任何数据可读） ========
    else if (bytesRead <= 0) {
        memset(outBuf, 0, audioDataSize); // 写入静音
        ctx->fadeState = 1;
        ctx->fadeFrameCount = 0;
    }
    // ================================================

    // 正常状态下重置
    if (framesRead >= framesNeeded && ctx->fadeState != 1) {
        ctx->fadeState = 0;
    }

    return AUDIO_DATA_CALLBACK_RESULT_VALID;
}

// ======================== Socket 缓冲区排空 ========================
// 恢复同步时调用：丢弃积压在 socket 缓冲区中的所有旧数据包
// 注意：不能用 SO_RCVTIMEO=0（POSIX 语义是"无限阻塞"而非"非阻塞"），
// 必须用 O_NONBLOCK，否则 drainSocket 会永久挂死 UDP 线程，导致 Stop 永不返回
static void drainSocket(int sock) {
    int flags = fcntl(sock, F_GETFL, 0);
    if (flags < 0) return;
    fcntl(sock, F_SETFL, flags | O_NONBLOCK);

    uint8_t dummy[2048]; // 排空用小缓冲即可（循环多次读取），避免 64KB 栈数组
    int drained = 0;
    // 循环读取直到缓冲区为空（EAGAIN）或达到安全上限
    while (recvfrom(sock, dummy, sizeof(dummy), 0, nullptr, nullptr) > 0) {
        if (++drained > 500) break; // 安全上限：防止意外死循环
    }

    fcntl(sock, F_SETFL, flags); // 恢复原标志
}

// ======================== UDP 接收线程 ========================
static void UdpReceiveLoop(NativeAudioContext *ctx) {
    // 64KB 接收缓冲放堆上（线程栈默认较小，避免栈溢出风险），仅分配一次
    std::vector<uint8_t> recvBuf(MAX_PACKET_SIZE);

    while (ctx->running.load()) {
        struct sockaddr_in senderAddr;
        socklen_t addrLen = sizeof(senderAddr);
        ssize_t n = recvfrom(ctx->udpSocket, recvBuf.data(), recvBuf.size(), 0,
                             (struct sockaddr *)&senderAddr, &addrLen);
        if (n <= HEADER_SIZE) continue;

        // 解析包头（44字节，所有字段小端序）
        uint8_t msgType = recvBuf[0];
        if (msgType != 1) continue; // 只处理 AUDIO_DATA

        uint8_t encoding = recvBuf[3];
        // 时间戳字段（用于延迟测量）——用 memcpy 避免非对齐指针读写（UB）
        int64_t captureTime = 0, encodeTime = 0, sendTime = 0;
        int32_t sampleRate = 0;
        int32_t payloadLen = 0;
        memcpy(&captureTime, recvBuf.data() + 8, 8);    // WASAPI采集时间
        memcpy(&encodeTime,  recvBuf.data() + 16, 8);   // PC端编码完成时间
        memcpy(&sendTime,    recvBuf.data() + 24, 8);   // PC端发送时间
        memcpy(&sampleRate,  recvBuf.data() + 32, 4);   // 采样率
        memcpy(&payloadLen,  recvBuf.data() + 40, 4);   // 负载长度

        if (payloadLen <= 0 || HEADER_SIZE + payloadLen > n) continue;

        uint8_t *payload = recvBuf.data() + HEADER_SIZE;

        // 记录 PC 地址（用于后续回复）
        inet_ntop(AF_INET, &senderAddr.sin_addr, ctx->pcAddress, sizeof(ctx->pcAddress));

        // ======== 延迟分项统计 ========
        // 使用gettimeofday获取毫秒级电话当前时间
        struct timeval tv;
        gettimeofday(&tv, nullptr);
        int64_t phoneNowMs = tv.tv_sec * 1000LL + tv.tv_usec / 1000;

        // ======== 渲染器存活检测（每2秒检查一次） ========
        // B站等App播放时系统可能静音我们的AudioRenderer但不发中断事件
        // 定期重启渲染器，确保它始终处于播放状态
        static int64_t lastRendererCheck = 0;
        if (ctx->renderer && phoneNowMs - lastRendererCheck > 2000) {
            OH_AudioRenderer_Start(ctx->renderer);
            lastRendererCheck = phoneNowMs;
        }
        // ==============================================
        int64_t offset = ctx->clockOffset.load(std::memory_order_acquire);

        // PC处理延迟: encodeTime - captureTime（已含 WASAPI 采集排队）
        // Opus 模式额外 +20ms 帧对齐时长（960 样本 @48kHz），使延迟更接近真实感知值
        int pcProcessMs = (int)(encodeTime - captureTime);
        if (encoding == 1) pcProcessMs += 20;
        ctx->latestPcProcessMs.store(pcProcessMs, std::memory_order_release);
        // 网络延迟: phoneNow - offset - sendTime (偏移校准后的单向网络延迟)
        int networkMs = (int)(phoneNowMs - offset - sendTime);
        if (networkMs < 0) networkMs = 0; // 时钟偏差可能导致微小负值
        ctx->latestNetworkMs.store(networkMs, std::memory_order_release);
        // 缓冲排队延迟: 基于pcmRing可用字节数估算 (bytes / (48000*2*2/1000) = bytes/192 ms)
        int bufferMs = ctx->pcmRing.available() / 192;
        if (bufferMs < 0) bufferMs = 0;
        ctx->latestPcmBufferMs.store(bufferMs, std::memory_order_release);
        ctx->latencyUpdateTime.store(phoneNowMs, std::memory_order_release);

        // 每5秒刷新一次 OHAudio 渲染延迟（跟踪音频路由变化：扬声器→蓝牙→耳机）
        if (ctx->renderer && phoneNowMs - ctx->lastLatencyQueryTime > 5000) {
            int32_t newLatency = 0;
            if (OH_AudioRenderer_GetLatency(ctx->renderer,
                    AUDIOSTREAM_LATENCY_TYPE_ALL, &newLatency) == AUDIOSTREAM_SUCCESS && newLatency > 0) {
                ctx->rendererLatencyMs = newLatency;
            }
            ctx->lastLatencyQueryTime = phoneNowMs;
        }
        // ================================

        // ======== Underrun 恢复检测 ========
        // 比较 pcmRing 的 underrun epoch 与写入端记录的 epoch：
        // gap > 200 空读（约2秒）才触发恢复，避免网络微抖动误触发
        uint32_t currentEpoch = ctx->pcmRing.getUnderrunEpoch();
        uint32_t lastEpoch = ctx->writeEpoch.load(std::memory_order_acquire);
        if (currentEpoch > lastEpoch && (currentEpoch - lastEpoch) > 200) {
            // 真实 underrun → 完整恢复
            drainSocket(ctx->udpSocket);
            ctx->pcmRing.clear();
            {
                std::lock_guard<std::mutex> lock(ctx->inputMtx);
                while (!ctx->inputQueue.empty()) {
                    ctx->inputQueue.pop();
                }
            }
            ctx->writeEpoch.store(currentEpoch, std::memory_order_release);
            continue;  // 跳过当前过期包，重新接收
        }
        // =====================================

        // ======== 缓冲上限保护 ========
        // 防止时钟漂移或网络突发导致 pcmRing 无限堆积
        // 超过用户设置的缓冲上限时直接丢弃当前帧，让 OHAudio 消费降下来
        int currentBufferMs = ctx->pcmRing.available() / 192;
        int capMs = ctx->maxBufferMs.load(std::memory_order_acquire);
        if (capMs > 0 && currentBufferMs > capMs) {
            continue; // 丢帧，跳过写入
        }
        // ================================

        if (encoding == 0) {
            // PCM: 直接写入环形缓冲区
            ctx->pcmRing.write(payload, payloadLen);
        } else if (encoding == 1) {
            // Opus: 送入解码队列（队列有上限，解码拥塞时丢新帧，防止内存无界增长）
            std::vector<uint8_t> opusFrame(payload, payload + payloadLen);
            {
                std::lock_guard<std::mutex> lock(ctx->inputMtx);
                if (ctx->inputQueue.size() < MAX_QUEUED_OPUS_FRAMES) {
                    ctx->inputQueue.push(std::move(opusFrame));
                }
            }
            ctx->inputCv.notify_one();
        }

        // 同步 writeEpoch（每次成功收包后拉近 gap，防止累积）
        if (ctx->writeEpoch.load(std::memory_order_relaxed) < currentEpoch) {
            ctx->writeEpoch.store(currentEpoch, std::memory_order_release);
        }
        // ADPCM (encoding==2) 暂不处理（可后续在 C++ 层实现）
    }
}

// ======================== Opus 解码器生命周期 ========================
// 编码热切换（Opus ↔ 非 Opus）与全局停止共用。
// 关键：必须先置 decodeActive=false 唤醒解码线程退出并 join，
// 再销毁 codec，避免 OnCodecNewOutput 回调与 PCM 直写构成 pcmRing 双 writer。
static void StopOpusDecoder(NativeAudioContext *ctx) {
    ctx->decodeActive.store(false, std::memory_order_release);
    ctx->inputCv.notify_all();
    ctx->cvInput.notify_all();

    if (ctx->decodeThread && ctx->decodeThread->joinable()) {
        ctx->decodeThread->join(); // 最坏 ~250ms（200ms cvInput 超时 + 50ms inputCv 超时）
    }
    delete ctx->decodeThread;
    ctx->decodeThread = nullptr;

    if (ctx->codec) {
        OH_AudioCodec_Flush(ctx->codec);
        OH_AudioCodec_Stop(ctx->codec);
        OH_AudioCodec_Destroy(ctx->codec); // 同步 API：返回后不再有 codec 回调
        ctx->codec = nullptr;
    }

    {
        std::lock_guard<std::mutex> lock(ctx->inputMtx);
        while (!ctx->inputQueue.empty()) ctx->inputQueue.pop();
    }
    {
        std::lock_guard<std::mutex> lock(ctx->codecMtx);
        ctx->inputReady = false;
        ctx->inputBuffer = nullptr;
        ctx->inputIndex = 0;
        ctx->pendingInputs = 0;
    }
}

static bool StartOpusDecoder(NativeAudioContext *ctx) {
    if (ctx->codec) return true; // 已存在（防御）

    ctx->codec = OH_AudioCodec_CreateByMime(OH_AVCODEC_MIMETYPE_AUDIO_OPUS, false);
    if (!ctx->codec) return false;

    OH_AVCodecCallback codecCb;
    codecCb.onError = OnCodecError;
    codecCb.onStreamChanged = OnCodecStreamChanged;
    codecCb.onNeedInputBuffer = OnCodecNeedInput;
    codecCb.onNewOutputBuffer = OnCodecNewOutput;
    OH_AudioCodec_RegisterCallback(ctx->codec, codecCb, ctx);

    OH_AVFormat *fmt = OH_AVFormat_Create();
    OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_AUD_SAMPLE_RATE, ctx->sampleRate);
    OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_AUD_CHANNEL_COUNT, ctx->channels);
    OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_MAX_INPUT_SIZE, MAX_PACKET_SIZE);
    OH_AudioCodec_Configure(ctx->codec, fmt);
    OH_AVFormat_Destroy(fmt);

    OH_AudioCodec_Prepare(ctx->codec);
    OH_AudioCodec_Start(ctx->codec);

    ctx->decodeActive.store(true, std::memory_order_release);
    ctx->decodeThread = new std::thread(OpusDecodeLoop, ctx);
    return true;
}

// ======================== 全局停止（停止全部管线并复位） ========================
// g_ctx 单例常驻、永不 delete：OHAudio 中断/写数据/错误回调与 codec 回调由
// 系统线程异步派发，释放 g_ctx 会形成 use-after-free。
// 停止顺序：置 running=false → join UDP 线程(≤1s) → 停解码器 → 停渲染器 → 关 socket → 复位。
static void StopNativeAudio(NativeAudioContext *ctx) {
    ctx->running.store(false);

    // UDP 线程：recvfrom 1s 超时，最坏 1s 内退出
    if (ctx->udpThread && ctx->udpThread->joinable()) {
        ctx->udpThread->join();
    }
    delete ctx->udpThread;
    ctx->udpThread = nullptr;

    // Opus 解码器（decodeActive=false 唤醒线程退出）
    StopOpusDecoder(ctx);

    // OHAudio 渲染器：Release 为同步操作，返回后不再有写数据回调
    if (ctx->renderer) {
        OH_AudioRenderer_Stop(ctx->renderer);
        OH_AudioRenderer_Release(ctx->renderer);
        ctx->renderer = nullptr;
    }

    // 关闭 UDP socket
    if (ctx->udpSocket >= 0) {
        close(ctx->udpSocket);
        ctx->udpSocket = -1;
    }

    // 复位（此刻无任何线程活跃，无并发）
    ctx->pcmRing.clear();
    ctx->writeEpoch.store(ctx->pcmRing.getUnderrunEpoch(), std::memory_order_release);
    ctx->decodeActive.store(false, std::memory_order_release);
}

// ======================== NAPI 函数 ========================

static napi_value NativeAudioInit(napi_env env, napi_callback_info info) {
    size_t argc = 2;
    napi_value args[2] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    int sampleRate = 48000, channels = 2;
    if (argc >= 1) napi_get_value_int32(env, args[0], &sampleRate);
    if (argc >= 2) napi_get_value_int32(env, args[1], &channels);

    // g_ctx 单例常驻：首次创建，后续复用（不 delete，避免异步回调 use-after-free）
    if (!g_ctx) {
        g_ctx = new NativeAudioContext();
    } else if (g_ctx->udpSocket >= 0 || g_ctx->renderer != nullptr || g_ctx->codec != nullptr) {
        // 上次未完全停止（异常路径），防御性停止后复用
        StopNativeAudio(g_ctx);
    }

    g_ctx->sampleRate = sampleRate;
    g_ctx->channels = channels;

    // 创建 UDP socket
    g_ctx->udpSocket = socket(AF_INET, SOCK_DGRAM, 0);
    if (g_ctx->udpSocket < 0) {
        napi_value r;
        napi_create_int32(env, -2, &r);
        return r;
    }

    // 绑定 UDP 端口
    struct sockaddr_in bindAddr;
    memset(&bindAddr, 0, sizeof(bindAddr));
    bindAddr.sin_family = AF_INET;
    bindAddr.sin_addr.s_addr = INADDR_ANY;
    bindAddr.sin_port = htons(g_ctx->udpPort);

    if (bind(g_ctx->udpSocket, (struct sockaddr *)&bindAddr, sizeof(bindAddr)) < 0) {
        close(g_ctx->udpSocket);
        g_ctx->udpSocket = -1;
        napi_value r;
        napi_create_int32(env, -3, &r);
        return r;
    }

    // 设置接收超时（防止线程无法退出）
    struct timeval tv;
    tv.tv_sec = 1;
    tv.tv_usec = 0;
    setsockopt(g_ctx->udpSocket, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));

    // 创建 OHAudio 渲染器
    OH_AudioStreamBuilder *builder = nullptr;
    OH_AudioStreamBuilder_Create(&builder, AUDIOSTREAM_TYPE_RENDERER);
    OH_AudioStreamBuilder_SetSamplingRate(builder, sampleRate);
    OH_AudioStreamBuilder_SetChannelCount(builder, channels);
    OH_AudioStreamBuilder_SetSampleFormat(builder, AUDIOSTREAM_SAMPLE_S16LE);
    OH_AudioStreamBuilder_SetEncodingType(builder, AUDIOSTREAM_ENCODING_TYPE_RAW);
    // GAME 优先级高于 MUSIC/MOVIE，不会被视频App Duck
    OH_AudioStreamBuilder_SetRendererInfo(builder, AUDIOSTREAM_USAGE_GAME);
    // 独立模式：不参与系统音频焦点竞争，与B站同时发声
    OH_AudioStreamBuilder_SetRendererInterruptMode(builder, AUDIOSTREAM_INTERRUPT_MODE_INDEPENDENT);

    // 使用新版 API（since 12）设置 writeData 回调
    OH_AudioStreamBuilder_SetRendererWriteDataCallback(builder, OnAudioWriteData, g_ctx);

    // ======== 动态加载新版 API 回调（参考 moonlight dlsym 模式） ========
    LoadAudioApis();
    // 注册中断回调：系统事件（B站播放/停止、来电等）主动通知
    if (g_pfnSetRendererInterruptCb) {
        g_pfnSetRendererInterruptCb(builder, OnAudioInterrupt, g_ctx);
    }
    // 注册错误回调：连续错误时自动标记 needRestart
    if (g_pfnSetRendererErrorCb) {
        g_pfnSetRendererErrorCb(builder, OnAudioError, g_ctx);
    }
    // 注册输出设备变更回调：扬声器↔蓝牙↔耳机切换通知
    if (g_pfnSetRendererDeviceChangeCb) {
        g_pfnSetRendererDeviceChangeCb(builder, OnAudioDeviceChange, g_ctx);
    }
    // ================================================================

    int32_t ret = OH_AudioStreamBuilder_GenerateRenderer(builder, &g_ctx->renderer);
    OH_AudioStreamBuilder_Destroy(builder);

    if (ret != AUDIOSTREAM_SUCCESS || !g_ctx->renderer) {
        if (g_ctx->renderer) {
            OH_AudioRenderer_Release(g_ctx->renderer);
            g_ctx->renderer = nullptr;
        }
        close(g_ctx->udpSocket);
        g_ctx->udpSocket = -1;
        napi_value r;
        napi_create_int32(env, -4, &r);
        return r;
    }

    napi_value r;
    napi_create_int32(env, 0, &r);
    return r;
}

static napi_value NativeAudioStart(napi_env env, napi_callback_info /*info*/) {
    if (!g_ctx) {
        napi_value r;
        napi_create_int32(env, -1, &r);
        return r;
    }

    // 幂等防御：若上次 Start 未配对的 Stop（异常路径），先完整停止再启动，
    // 避免重复创建 UDP 线程导致双线程 recvfrom 争抢数据
    if (g_ctx->udpThread != nullptr || g_ctx->decodeThread != nullptr) {
        StopNativeAudio(g_ctx);
    }

    g_ctx->running.store(true);
    g_ctx->pcmRing.clear();

    // 同步写入端 epoch 到 pcmRing 的当前 underrun epoch，
    // 避免跨 stop/start 周期误触发恢复。
    g_ctx->writeEpoch.store(g_ctx->pcmRing.getUnderrunEpoch(), std::memory_order_release);

    // 无条件创建 Opus 解码器：解码路径按 UDP 包内 encoding 字段分发
    // （ArkTS 端 nativeAudioSetEncoding(0) 写死，不代表只会收到 PCM）
    StartOpusDecoder(g_ctx);

    // 启动 UDP 接收线程
    g_ctx->udpThread = new std::thread(UdpReceiveLoop, g_ctx);

    // 启动 OHAudio 渲染器
    OH_AudioRenderer_Start(g_ctx->renderer);

    // 查询 OHAudio 真实渲染延迟（只查一次，避免频繁调用阻塞路由变更）
    int32_t latencyMs = 0;
    OH_AudioStream_Result latRet = OH_AudioRenderer_GetLatency(
        g_ctx->renderer, AUDIOSTREAM_LATENCY_TYPE_ALL, &latencyMs);
    if (latRet == AUDIOSTREAM_SUCCESS && latencyMs > 0) {
        g_ctx->rendererLatencyMs = latencyMs;
    }

    napi_value r;
    napi_create_int32(env, 0, &r);
    return r;
}

static napi_value NativeAudioStop(napi_env env, napi_callback_info /*info*/) {
    if (!g_ctx) {
        napi_value r;
        napi_create_int32(env, -1, &r);
        return r;
    }

    StopNativeAudio(g_ctx);

    napi_value r;
    napi_create_int32(env, 0, &r);
    return r;
}

static napi_value NativeAudioSetEncoding(napi_env env, napi_callback_info info) {
    size_t argc = 1;
    napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    int encType = 0;
    if (argc >= 1) napi_get_value_int32(env, args[0], &encType);

    if (g_ctx) {
        // 仅更新编码类型并清空缓冲。
        // 注意：解码器始终存在（NativeAudioStart 无条件创建），解码路径按 UDP 包内
        // encoding 字段分发，此处不销毁解码器——否则切换瞬间到达的 Opus 包无人消费。
        // 双 writer 竞态已由 pcmRing 互斥锁消除，残留帧由 clear() 丢弃。
        g_ctx->encodingType.store(encType, std::memory_order_release);
        g_ctx->pcmRing.clear();
        // 同步 writeEpoch，避免切编码时误触发恢复
        g_ctx->writeEpoch.store(g_ctx->pcmRing.getUnderrunEpoch(), std::memory_order_release);
    }

    napi_value r;
    napi_get_undefined(env, &r);
    return r;
}

static napi_value NativeAudioGetQueueSize(napi_env env, napi_callback_info /*info*/) {
    int size = 0;
    if (g_ctx) {
        size = g_ctx->pcmRing.available();
    }
    napi_value r;
    napi_create_int32(env, size, &r);
    return r;
}

// 设置时钟偏移（手机时间 - PC时间，单位ms，由TIME_SYNC协议校准）
static napi_value NativeAudioSetClockOffset(napi_env env, napi_callback_info info) {
    size_t argc = 1;
    napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    int64_t offset = 0;
    if (argc >= 1) napi_get_value_int64(env, args[0], &offset);

    if (g_ctx) {
        g_ctx->clockOffset.store(offset, std::memory_order_release);
    }

    napi_value r;
    napi_get_undefined(env, &r);
    return r;
}

// 设置播放音量（0.0 ~ 1.0）
static napi_value NativeAudioSetVolume(napi_env env, napi_callback_info info) {
    size_t argc = 1;
    napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    double volume = 1.0;
    if (argc >= 1) napi_get_value_double(env, args[0], &volume);

    if (g_ctx && g_ctx->renderer) {
        OH_AudioRenderer_SetVolume(g_ctx->renderer, (float)volume);
    }

    napi_value r;
    napi_get_undefined(env, &r);
    return r;
}

// 设置环形缓冲上限（由ArkTS端缓冲选择器下发）
static napi_value NativeAudioSetBufferMs(napi_env env, napi_callback_info info) {
    size_t argc = 1;
    napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    int ms = 100;
    if (argc >= 1) napi_get_value_int32(env, args[0], &ms);

    if (g_ctx) {
        g_ctx->maxBufferMs.store(ms, std::memory_order_release);
        g_ctx->pcmRing.clear();
        g_ctx->writeEpoch.store(g_ctx->pcmRing.getUnderrunEpoch(), std::memory_order_release);
    }

    napi_value r;
    napi_get_undefined(env, &r);
    return r;
}

// 获取最近一帧的延迟分项统计 (4个int32: pcProcess, network, buffer, unused/renderer)
// 返回 ArrayBuffer 包含 16 字节（4个int32 LE），对应 LATENCY_REPORT payload
static napi_value NativeAudioGetLatencyStats(napi_env env, napi_callback_info /*info*/) {
    int pcProcess = 0, network = 0, buffer = 0;
    if (g_ctx) {
        pcProcess = g_ctx->latestPcProcessMs.load(std::memory_order_acquire);
        network   = g_ctx->latestNetworkMs.load(std::memory_order_acquire);
        buffer    = g_ctx->latestPcmBufferMs.load(std::memory_order_acquire);
    }

    // 构造16字节payload: 4个int32 LE (与C#端OnLatencyReport 16字节格式对齐)
    uint8_t payload[16] = {0};
    int rendererMs = 0;
    if (g_ctx) rendererMs = g_ctx->rendererLatencyMs; // 真实 OHAudio 硬件延迟
    int32_t vals[4] = {network, pcProcess, buffer, rendererMs};
    memcpy(payload, vals, sizeof(vals));

    napi_value result;
    void *buf = nullptr;
    napi_create_arraybuffer(env, 16, &buf, &result);
    memcpy(buf, payload, 16);
    return result;
}

// ======================== 保留旧的 Opus 解码器 API（兼容性） ========================
// ======================== 模块注册 ========================
EXTERN_C_START
static napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor desc[] = {
        // 原生音频管线 API
        {"nativeAudioInit", nullptr, NativeAudioInit, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioStart", nullptr, NativeAudioStart, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioStop", nullptr, NativeAudioStop, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioSetEncoding", nullptr, NativeAudioSetEncoding, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioGetQueueSize", nullptr, NativeAudioGetQueueSize, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioSetClockOffset", nullptr, NativeAudioSetClockOffset, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioGetLatencyStats", nullptr, NativeAudioGetLatencyStats, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioSetBufferMs", nullptr, NativeAudioSetBufferMs, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioSetVolume", nullptr, NativeAudioSetVolume, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    napi_define_properties(env, exports, sizeof(desc) / sizeof(desc[0]), desc);
    return exports;
}
EXTERN_C_END

NAPI_MODULE(opus_decoder, Init)
