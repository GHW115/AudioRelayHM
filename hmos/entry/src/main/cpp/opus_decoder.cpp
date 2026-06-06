// 🤖 AI 辅助生成 — Claude (Anthropic)
// 项目: AudioRelayHM 鸿蒙↔Windows 音频串流

#include "napi/native_api.h"
#include "multimedia/player_framework/native_avcodec_audiocodec.h"
#include "multimedia/player_framework/native_avcodec_base.h"
#include "multimedia/player_framework/native_avformat.h"
#include "multimedia/player_framework/native_avbuffer.h"
#include <cstring>
#include <mutex>
#include <condition_variable>
#include <vector>
#include <thread>
#include <queue>

#define MAX_PACKET_SIZE 4000

struct DecoderContext {
    OH_AVCodec *codec = nullptr;
    int sampleRate = 48000;
    int channels = 2;
    bool running = false;
    std::thread *decodeThread = nullptr;

    // 输入输出队列
    std::mutex mtx;
    std::condition_variable cv;
    std::queue<std::vector<uint8_t>> inputQueue;
    std::queue<std::vector<uint8_t>> outputQueue;

    // OH_AudioCodec 同步
    std::mutex codecMtx;
    std::condition_variable cvInput;
    bool inputReady = false;
    uint32_t inputIndex = 0;
    OH_AVBuffer *inputBuffer = nullptr;
    int pendingInputs = 0; // 已推送但尚未收到输出的输入帧数

    void DecodeLoop();
    void FeedInput(const uint8_t *data, size_t size);
};

static void OnError(OH_AVCodec *, int32_t, void *) {}
static void OnStreamChanged(OH_AVCodec *, OH_AVFormat *, void *) {}

static void OnNeedInput(OH_AVCodec *, uint32_t index, OH_AVBuffer *buffer, void *userData) {
    auto *ctx = (DecoderContext *)userData;
    std::lock_guard<std::mutex> lock(ctx->codecMtx);
    ctx->inputIndex = index;
    ctx->inputBuffer = buffer;
    ctx->inputReady = true;
    ctx->cvInput.notify_one();
}

static void OnNewOutput(OH_AVCodec *codec, uint32_t index, OH_AVBuffer *buffer, void *userData) {
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
            ctx->cv.notify_one(); // 唤醒可能等待输出的 ReadOutput
        }
    }
    {
        std::lock_guard<std::mutex> lock(ctx->codecMtx);
        if (ctx->pendingInputs > 0) ctx->pendingInputs--;
    }
    OH_AudioCodec_FreeOutputBuffer(codec, index);
}

void DecoderContext::FeedInput(const uint8_t *data, size_t size) {
    // 等待 codec 提供输入缓冲区（带超时防止死锁）
    {
        std::unique_lock<std::mutex> lock(codecMtx);
        if (!cvInput.wait_for(lock, std::chrono::milliseconds(200),
                              [this] { return inputReady || !running; })) {
            return; // 超时，跳过本帧
        }
        if (!running) return;
        inputReady = false;
    }

    if (inputBuffer && size <= MAX_PACKET_SIZE) {
        uint8_t *buf = OH_AVBuffer_GetAddr(inputBuffer);
        if (buf) std::memcpy(buf, data, size);
        OH_AVCodecBufferAttr attr;
        attr.size = (int32_t)size; attr.offset = 0; attr.pts = 0;
        attr.flags = AVCODEC_BUFFER_FLAGS_NONE;
        OH_AVBuffer_SetBufferAttr(inputBuffer, &attr);
        {
            std::lock_guard<std::mutex> lock(codecMtx);
            pendingInputs++;
        }
        OH_AudioCodec_PushInputBuffer(codec, inputIndex);
    }
    // 不等待输出 —— OnNewOutput 回调会异步地将 PCM 放入 outputQueue
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
    cb.onError = OnError; cb.onStreamChanged = OnStreamChanged;
    cb.onNeedInputBuffer = OnNeedInput; cb.onNewOutputBuffer = OnNewOutput;
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

// 入队：立即返回，不阻塞
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
        std::memcpy(buf.data(), data, size);
        {
            std::lock_guard<std::mutex> lock(ctx->mtx);
            ctx->inputQueue.push(std::move(buf));
        }
        ctx->cv.notify_one();
    }
    napi_value r; napi_get_undefined(env, &r); return r;
}

// 出队：有数据返回 ArrayBuffer，没有返回 undefined
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
    std::memcpy(buf, out.data(), out.size());
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

EXTERN_C_START
static napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor desc[] = {
        {"createDecoder", nullptr, CreateDecoder, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"pushInput", nullptr, PushInput, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"readOutput", nullptr, ReadOutput, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"destroyDecoder", nullptr, DestroyDecoder, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    napi_define_properties(env, exports, sizeof(desc) / sizeof(desc[0]), desc);
    return exports;
}
EXTERN_C_END

NAPI_MODULE(opus_decoder, Init)
