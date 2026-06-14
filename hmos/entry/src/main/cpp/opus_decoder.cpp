// 🤖 AI 辅助生成 — Claude (Anthropic)
// 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
// 全 NAPI 原生音频管线：UDP接收 → Opus解码 → OHAudio渲染（绕过JS事件循环）

#include "napi/native_api.h"
#include "multimedia/player_framework/native_avcodec_audiocodec.h"
#include "multimedia/player_framework/native_avcodec_base.h"
#include "multimedia/player_framework/native_avformat.h"
#include "multimedia/player_framework/native_avbuffer.h"
#include "ohaudio/ohaudio.h"
#include <cstring>
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

#define MAX_PACKET_SIZE 65536
#define HEADER_SIZE 44
#define RING_BUFFER_CAPACITY (1024 * 1024) // 1MB ring buffer (~5.4s @ 48kHz/2ch/16bit)

// ======================== 环形缓冲区 ========================
class AudioRingBuffer {
public:
    AudioRingBuffer(int capacity = RING_BUFFER_CAPACITY)
        : buf_(capacity, 0), capacity_(capacity), readPos_(0), writePos_(0), count_(0) {}

    int write(const uint8_t *data, int size) {
        std::lock_guard<std::mutex> lock(mtx_);
        int written = 0;
        for (int i = 0; i < size; i++) {
            if (count_ >= capacity_) break; // 满则丢弃多余数据
            buf_[writePos_] = data[i];
            writePos_ = (writePos_ + 1) % capacity_;
            count_++;
            written++;
        }
        return written;
    }

    int read(uint8_t *data, int size) {
        std::lock_guard<std::mutex> lock(mtx_);
        int bytesRead = 0;
        for (int i = 0; i < size; i++) {
            if (count_ <= 0) {
                // 数据不足，填零
                memset(data + i, 0, size - i);
                return bytesRead;
            }
            data[i] = buf_[readPos_];
            readPos_ = (readPos_ + 1) % capacity_;
            count_--;
            bytesRead++;
        }
        return bytesRead;
    }

    void clear() {
        std::lock_guard<std::mutex> lock(mtx_);
        readPos_ = writePos_ = count_ = 0;
    }

    int available() const { return count_; }

private:
    std::vector<uint8_t> buf_;
    int capacity_, readPos_, writePos_, count_;
    std::mutex mtx_;
};

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

    // OHAudio 渲染器
    OH_AudioRenderer *renderer = nullptr;
    int sampleRate = 48000;
    int channels = 2;
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

static void OnCodecNewOutput(OH_AudioCodec *codec, uint32_t index, OH_AVBuffer *buffer, void *userData) {
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
    while (ctx->running.load()) {
        std::vector<uint8_t> input;
        {
            std::unique_lock<std::mutex> lock(ctx->inputMtx);
            ctx->inputCv.wait_for(lock, std::chrono::milliseconds(50),
                [ctx] { return !ctx->inputQueue.empty() || !ctx->running.load(); });
            if (!ctx->running.load()) break;
            if (ctx->inputQueue.empty()) continue;
            input = std::move(ctx->inputQueue.front());
            ctx->inputQueue.pop();
        }

        // Feed input to codec
        {
            std::unique_lock<std::mutex> lock(ctx->codecMtx);
            if (!ctx->cvInput.wait_for(lock, std::chrono::milliseconds(200),
                [ctx] { return ctx->inputReady || !ctx->running.load(); })) {
                continue;
            }
            if (!ctx->running.load()) break;
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

// ======================== OHAudio OnWriteData 回调（系统音频线程） ========================
static OH_AudioData_Callback_Result OnAudioWriteData(
    OH_AudioRenderer *renderer, void *userData,
    void *audioData, int32_t audioDataSize)
{
    auto *ctx = (NativeAudioContext *)userData;
    int bytesRead = ctx->pcmRing.read((uint8_t *)audioData, audioDataSize);
    if (bytesRead < audioDataSize) {
        // 数据不足的部分已由 read() 填零
    }
    return AUDIO_DATA_CALLBACK_RESULT_VALID;
}

// ======================== UDP 接收线程 ========================
static void UdpReceiveLoop(NativeAudioContext *ctx) {
    uint8_t recvBuf[MAX_PACKET_SIZE];

    while (ctx->running.load()) {
        struct sockaddr_in senderAddr;
        socklen_t addrLen = sizeof(senderAddr);
        ssize_t n = recvfrom(ctx->udpSocket, recvBuf, sizeof(recvBuf), 0,
                             (struct sockaddr *)&senderAddr, &addrLen);
        if (n <= HEADER_SIZE) continue;

        // 解析包头
        uint8_t msgType = recvBuf[0];
        if (msgType != 1) continue; // 只处理 AUDIO_DATA

        uint8_t encoding = recvBuf[3];
        int32_t sampleRate = *(int32_t *)(recvBuf + 16);
        uint8_t channels = recvBuf[20];
        int32_t payloadLen = *(int32_t *)(recvBuf + 40);

        if (payloadLen <= 0 || HEADER_SIZE + payloadLen > n) continue;

        uint8_t *payload = recvBuf + HEADER_SIZE;

        // 记录 PC 地址（用于后续回复）
        inet_ntop(AF_INET, &senderAddr.sin_addr, ctx->pcAddress, sizeof(ctx->pcAddress));

        if (encoding == 0) {
            // PCM: 直接写入环形缓冲区
            ctx->pcmRing.write(payload, payloadLen);
        } else if (encoding == 1) {
            // Opus: 送入解码队列
            std::vector<uint8_t> opusFrame(payload, payload + payloadLen);
            {
                std::lock_guard<std::mutex> lock(ctx->inputMtx);
                ctx->inputQueue.push(std::move(opusFrame));
            }
            ctx->inputCv.notify_one();
        }
        // ADPCM (encoding==2) 暂不处理（可后续在 C++ 层实现）
    }
}

// ======================== NAPI 函数 ========================

static napi_value NativeAudioInit(napi_env env, napi_callback_info info) {
    size_t argc = 2;
    napi_value args[2] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    int sampleRate = 48000, channels = 2;
    if (argc >= 1) napi_get_value_int32(env, args[0], &sampleRate);
    if (argc >= 2) napi_get_value_int32(env, args[1], &channels);

    if (g_ctx) {
        napi_value r;
        napi_create_int32(env, -1, &r);
        return r;
    }

    g_ctx = new NativeAudioContext();
    g_ctx->sampleRate = sampleRate;
    g_ctx->channels = channels;

    // 创建 UDP socket
    g_ctx->udpSocket = socket(AF_INET, SOCK_DGRAM, 0);
    if (g_ctx->udpSocket < 0) {
        delete g_ctx;
        g_ctx = nullptr;
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
        delete g_ctx;
        g_ctx = nullptr;
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
    OH_AudioStreamBuilder_SetSampleFormat(builder, AUDIOSTREAM_SAMPLEFMT_S16LE);
    OH_AudioStreamBuilder_SetEncodingType(builder, AUDIOSTREAM_ENCODING_PCM);
    OH_AudioStreamBuilder_SetRendererInfo(builder, OH_AUDIOSTREAM_USAGE_MEDIA);

    OH_AudioRenderer_Callback cb;
    cb.onWriteData = OnAudioWriteData;
    cb.onReadData = nullptr;
    cb.onStreamEvent = nullptr;
    cb.onInterruptEvent = nullptr;
    cb.onError = nullptr;
    OH_AudioStreamBuilder_SetRendererCallback(builder, cb, g_ctx);

    int32_t ret = OH_AudioStreamBuilder_GenerateRenderer(builder, &g_ctx->renderer);
    OH_AudioStreamBuilder_Destroy(builder);

    if (ret != AUDIO_SUCCESS || !g_ctx->renderer) {
        close(g_ctx->udpSocket);
        delete g_ctx;
        g_ctx = nullptr;
        napi_value r;
        napi_create_int32(env, -4, &r);
        return r;
    }

    napi_value r;
    napi_create_int32(env, 0, &r);
    return r;
}

static napi_value NativeAudioStart(napi_env env, napi_callback_info info) {
    if (!g_ctx) {
        napi_value r;
        napi_create_int32(env, -1, &r);
        return r;
    }

    g_ctx->running.store(true);
    g_ctx->pcmRing.clear();

    // 初始化 Opus 解码器
    g_ctx->codec = OH_AudioCodec_CreateByMime(OH_AVCODEC_MIMETYPE_AUDIO_OPUS, false);
    if (g_ctx->codec) {
        OH_AVCodecCallback codecCb;
        codecCb.onError = OnCodecError;
        codecCb.onStreamChanged = OnCodecStreamChanged;
        codecCb.onNeedInputBuffer = OnCodecNeedInput;
        codecCb.onNewOutputBuffer = OnCodecNewOutput;
        OH_AudioCodec_RegisterCallback(g_ctx->codec, codecCb, g_ctx);

        OH_AVFormat *fmt = OH_AVFormat_Create();
        OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_AUD_SAMPLE_RATE, g_ctx->sampleRate);
        OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_AUD_CHANNEL_COUNT, g_ctx->channels);
        OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_MAX_INPUT_SIZE, MAX_PACKET_SIZE);
        OH_AudioCodec_Configure(g_ctx->codec, fmt);
        OH_AVFormat_Destroy(fmt);

        OH_AudioCodec_Prepare(g_ctx->codec);
        OH_AudioCodec_Start(g_ctx->codec);

        g_ctx->decodeThread = new std::thread(OpusDecodeLoop, g_ctx);
    }

    // 启动 UDP 接收线程
    g_ctx->udpThread = new std::thread(UdpReceiveLoop, g_ctx);

    // 启动 OHAudio 渲染器
    OH_AudioRenderer_Start(g_ctx->renderer);

    napi_value r;
    napi_create_int32(env, 0, &r);
    return r;
}

static napi_value NativeAudioStop(napi_env env, napi_callback_info info) {
    if (!g_ctx) {
        napi_value r;
        napi_create_int32(env, -1, &r);
        return r;
    }

    g_ctx->running.store(false);

    // 停止 UDP 线程
    if (g_ctx->udpThread && g_ctx->udpThread->joinable()) {
        g_ctx->udpThread->join();
    }
    delete g_ctx->udpThread;
    g_ctx->udpThread = nullptr;

    // 停止 Opus 解码
    if (g_ctx->decodeThread && g_ctx->decodeThread->joinable()) {
        g_ctx->decodeThread->join();
    }
    delete g_ctx->decodeThread;
    g_ctx->decodeThread = nullptr;

    if (g_ctx->codec) {
        OH_AudioCodec_Flush(g_ctx->codec);
        OH_AudioCodec_Stop(g_ctx->codec);
        OH_AudioCodec_Destroy(g_ctx->codec);
        g_ctx->codec = nullptr;
    }

    // 停止 OHAudio 渲染器
    if (g_ctx->renderer) {
        OH_AudioRenderer_Stop(g_ctx->renderer);
        OH_AudioRenderer_Destroy(g_ctx->renderer);
        g_ctx->renderer = nullptr;
    }

    // 关闭 UDP socket
    if (g_ctx->udpSocket >= 0) {
        close(g_ctx->udpSocket);
        g_ctx->udpSocket = -1;
    }

    delete g_ctx;
    g_ctx = nullptr;

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
        g_ctx->encodingType.store(encType);
        g_ctx->pcmRing.clear();
    }

    napi_value r;
    napi_get_undefined(env, &r);
    return r;
}

static napi_value NativeAudioGetQueueSize(napi_env env, napi_callback_info info) {
    int size = 0;
    if (g_ctx) {
        size = g_ctx->pcmRing.available();
    }
    napi_value r;
    napi_create_int32(env, size, &r);
    return r;
}

// ======================== 保留旧的 Opus 解码器 API（兼容性） ========================

struct DecoderContext {
    OH_AVCodec *codec = nullptr;
    int sampleRate = 48000;
    int channels = 2;
    bool running = false;
    std::thread *decodeThread = nullptr;
    std::mutex mtx;
    std::condition_variable cv;
    std::queue<std::vector<uint8_t>> inputQueue;
    std::queue<std::vector<uint8_t>> outputQueue;
    std::mutex codecMtx;
    std::condition_variable cvInput;
    bool inputReady = false;
    uint32_t inputIndex = 0;
    OH_AVBuffer *inputBuffer = nullptr;
    int pendingInputs = 0;
    void DecodeLoop();
    void FeedInput(const uint8_t *data, size_t size);
};

static void LegacyOnError(OH_AVCodec *, int32_t, void *) {}
static void LegacyOnStreamChanged(OH_AVCodec *, OH_AVFormat *, void *) {}

static void LegacyOnNeedInput(OH_AVCodec *, uint32_t index, OH_AVBuffer *buffer, void *userData) {
    auto *ctx = (DecoderContext *)userData;
    std::lock_guard<std::mutex> lock(ctx->codecMtx);
    ctx->inputIndex = index;
    ctx->inputBuffer = buffer;
    ctx->inputReady = true;
    ctx->cvInput.notify_one();
}

static void LegacyOnNewOutput(OH_AudioCodec *codec, uint32_t index, OH_AVBuffer *buffer, void *userData) {
    auto *ctx = (DecoderContext *)userData;
    OH_AVCodecBufferAttr attr;
    OH_AVBuffer_GetBufferAttr(buffer, &attr);
    if (attr.size > 0 && !(attr.flags & AVCODEC_BUFFER_FLAGS_EOS)) {
        uint8_t *data = OH_AVBuffer_GetAddr(buffer);
        if (data) {
            std::vector<uint8_t> pcm(data + attr.offset, data + attr.offset + attr.size);
            {
                std::lock_guard<std::mutex> lock(ctx->mtx);
                ctx->outputQueue.push(std::move(pcm));
            }
            ctx->cv.notify_one();
        }
    }
    {
        std::lock_guard<std::mutex> lock(ctx->codecMtx);
        if (ctx->pendingInputs > 0) ctx->pendingInputs--;
    }
    OH_AudioCodec_FreeOutputBuffer(codec, index);
}

void DecoderContext::FeedInput(const uint8_t *data, size_t size) {
    {
        std::unique_lock<std::mutex> lock(codecMtx);
        if (!cvInput.wait_for(lock, std::chrono::milliseconds(200),
                              [this] { return inputReady || !running; })) return;
        if (!running) return;
        inputReady = false;
    }
    if (inputBuffer && size <= MAX_PACKET_SIZE) {
        uint8_t *buf = OH_AVBuffer_GetAddr(inputBuffer);
        if (buf) memcpy(buf, data, size);
        OH_AVCodecBufferAttr attr;
        attr.size = (int32_t)size; attr.offset = 0; attr.pts = 0;
        attr.flags = AVCODEC_BUFFER_FLAGS_NONE;
        OH_AVBuffer_SetBufferAttr(inputBuffer, &attr);
        { std::lock_guard<std::mutex> lock(codecMtx); pendingInputs++; }
        OH_AudioCodec_PushInputBuffer(codec, inputIndex);
    }
}

void DecoderContext::DecodeLoop() {
    while (running) {
        std::vector<uint8_t> input;
        {
            std::unique_lock<std::mutex> lock(mtx);
            cv.wait_for(lock, std::chrono::milliseconds(50),
                        [this] { return !inputQueue.empty() || !running; });
            if (!running) break;
            if (inputQueue.empty()) continue;
            input = std::move(inputQueue.front());
            inputQueue.pop();
        }
        FeedInput(input.data(), input.size());
    }
}

static napi_value CreateDecoder(napi_env env, napi_callback_info info) {
    size_t argc = 2; napi_value args[2] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);
    int sr = 48000, ch = 2;
    napi_get_value_int32(env, args[0], &sr);
    napi_get_value_int32(env, args[1], &ch);
    auto *ctx = new DecoderContext();
    ctx->sampleRate = sr; ctx->channels = ch;
    ctx->codec = OH_AudioCodec_CreateByMime(OH_AVCODEC_MIMETYPE_AUDIO_OPUS, false);
    if (!ctx->codec) { delete ctx; napi_value r; napi_create_int32(env, -1, &r); return r; }
    OH_AVCodecCallback cb;
    cb.onError = LegacyOnError; cb.onStreamChanged = LegacyOnStreamChanged;
    cb.onNeedInputBuffer = LegacyOnNeedInput; cb.onNewOutputBuffer = LegacyOnNewOutput;
    OH_AudioCodec_RegisterCallback(ctx->codec, cb, ctx);
    OH_AVFormat *fmt = OH_AVFormat_Create();
    OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_AUD_SAMPLE_RATE, sr);
    OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_AUD_CHANNEL_COUNT, ch);
    OH_AVFormat_SetIntValue(fmt, OH_MD_KEY_MAX_INPUT_SIZE, MAX_PACKET_SIZE);
    int ret = OH_AudioCodec_Configure(ctx->codec, fmt);
    OH_AVFormat_Destroy(fmt);
    if (ret != AV_ERR_OK) { OH_AudioCodec_Destroy(ctx->codec); delete ctx; napi_value r; napi_create_int32(env, -1, &r); return r; }
    ret = OH_AudioCodec_Prepare(ctx->codec);
    if (ret != AV_ERR_OK) { OH_AudioCodec_Destroy(ctx->codec); delete ctx; napi_value r; napi_create_int32(env, -1, &r); return r; }
    ret = OH_AudioCodec_Start(ctx->codec);
    if (ret != AV_ERR_OK) { OH_AudioCodec_Destroy(ctx->codec); delete ctx; napi_value r; napi_create_int32(env, -1, &r); return r; }
    ctx->running = true;
    ctx->decodeThread = new std::thread(&DecoderContext::DecodeLoop, ctx);
    napi_value r; napi_create_int64(env, reinterpret_cast<int64_t>(ctx), &r); return r;
}

static napi_value PushInput(napi_env env, napi_callback_info info) {
    size_t argc = 2; napi_value args[2] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);
    int64_t handle = 0; napi_get_value_int64(env, args[0], &handle);
    auto *ctx = reinterpret_cast<DecoderContext *>(handle);
    if (!ctx || !ctx->running) { napi_value r; napi_get_undefined(env, &r); return r; }
    void *data = nullptr; size_t size = 0;
    napi_get_arraybuffer_info(env, args[1], &data, &size);
    if (data && size > 0) {
        std::vector<uint8_t> buf(size);
        memcpy(buf.data(), data, size);
        { std::lock_guard<std::mutex> lock(ctx->mtx); ctx->inputQueue.push(std::move(buf)); }
        ctx->cv.notify_one();
    }
    napi_value r; napi_get_undefined(env, &r); return r;
}

static napi_value ReadOutput(napi_env env, napi_callback_info info) {
    size_t argc = 1; napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);
    int64_t handle = 0; napi_get_value_int64(env, args[0], &handle);
    auto *ctx = reinterpret_cast<DecoderContext *>(handle);
    if (!ctx) { napi_value r; napi_get_undefined(env, &r); return r; }
    std::vector<uint8_t> out;
    {
        std::lock_guard<std::mutex> lock(ctx->mtx);
        if (!ctx->outputQueue.empty()) {
            out = std::move(ctx->outputQueue.front());
            ctx->outputQueue.pop();
        }
    }
    if (out.empty()) { napi_value r; napi_get_undefined(env, &r); return r; }
    void *buf = nullptr; napi_value result;
    napi_create_arraybuffer(env, out.size(), &buf, &result);
    memcpy(buf, out.data(), out.size());
    return result;
}

static napi_value DestroyDecoder(napi_env env, napi_callback_info info) {
    size_t argc = 1; napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);
    int64_t handle = 0; napi_get_value_int64(env, args[0], &handle);
    auto *ctx = reinterpret_cast<DecoderContext *>(handle);
    if (ctx) {
        ctx->running = false;
        ctx->cv.notify_one();
        if (ctx->decodeThread && ctx->decodeThread->joinable()) ctx->decodeThread->join();
        delete ctx->decodeThread;
        if (ctx->codec) { OH_AudioCodec_Flush(ctx->codec); OH_AudioCodec_Stop(ctx->codec); OH_AudioCodec_Destroy(ctx->codec); }
        delete ctx;
    }
    napi_value r; napi_get_undefined(env, &r); return r;
}

// ======================== 模块注册 ========================
EXTERN_C_START
static napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor desc[] = {
        // 旧 API（兼容）
        {"createDecoder", nullptr, CreateDecoder, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"pushInput", nullptr, PushInput, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"readOutput", nullptr, ReadOutput, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"destroyDecoder", nullptr, DestroyDecoder, nullptr, nullptr, nullptr, napi_default, nullptr},
        // 新 API（原生音频管线）
        {"nativeAudioInit", nullptr, NativeAudioInit, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioStart", nullptr, NativeAudioStart, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioStop", nullptr, NativeAudioStop, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioSetEncoding", nullptr, NativeAudioSetEncoding, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"nativeAudioGetQueueSize", nullptr, NativeAudioGetQueueSize, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    napi_define_properties(env, exports, sizeof(desc) / sizeof(desc[0]), desc);
    return exports;
}
EXTERN_C_END

NAPI_MODULE(opus_decoder, Init)
