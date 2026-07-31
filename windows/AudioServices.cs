// 音频服务：WASAPI 环回捕获 + 播放（VB-Cable 虚拟麦克风输出）
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Concentus;
using Concentus.Structs;
using Concentus.Enums;
using NAudio.Wave;

namespace AudioRelayWinUI;

// ========== 低延迟 WASAPI 环回捕获 ==========
public class LowLatencyLoopbackCapture : IDisposable {
    private NAudio.CoreAudioApi.MMDevice? _device;
    private NAudio.CoreAudioApi.AudioClient? _audioClient;
    private NAudio.Wave.WaveFormat? _format;
    private Thread? _captureThread;
    private volatile bool _stop;
    private System.Threading.EventWaitHandle? _eventHandle;

    public NAudio.Wave.WaveFormat WaveFormat => _format!;
    public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable;

    public void Start() {
        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        _device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        _audioClient = _device.AudioClient;
        _format = _audioClient.MixFormat;

        // 30ms 缓冲（100纳秒单位），平衡延迟和兼容性
        long bufferPeriod = 300_000; // 30ms
        _audioClient.Initialize(NAudio.CoreAudioApi.AudioClientShareMode.Shared,
            NAudio.CoreAudioApi.AudioClientStreamFlags.Loopback | NAudio.CoreAudioApi.AudioClientStreamFlags.EventCallback,
            bufferPeriod, 0, _format, Guid.Empty);

        _eventHandle = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset);
        _audioClient.SetEventHandle(_eventHandle.SafeWaitHandle.DangerousGetHandle());

        _stop = false;
        _captureThread = new Thread(CaptureLoop) { Priority = ThreadPriority.Highest, IsBackground = true };
        _captureThread.Start();
    }

    private void CaptureLoop() {
        var capture = _audioClient!.AudioCaptureClient;
        int frameBytes = _format!.Channels * _format.BitsPerSample / 8;
        _audioClient.Start();

        while (!_stop) {
            // 事件驱动：等待 WASAPI 通知数据就绪（比 1ms 轮询更高效）
            if (!_eventHandle!.WaitOne(100)) continue;

            int available = capture.GetNextPacketSize();
            while (available > 0) {
                var buffer = capture.GetBuffer(out int frames, out var flags);
                int bytes = frames * frameBytes;
                if (bytes > 0 && (flags & NAudio.CoreAudioApi.AudioClientBufferFlags.Silent) == 0) {
                    var data = new byte[bytes];
                    Marshal.Copy(buffer, data, 0, bytes);
                    DataAvailable?.Invoke(this, new NAudio.Wave.WaveInEventArgs(data, bytes));
                }
                capture.ReleaseBuffer(frames);
                available = capture.GetNextPacketSize();
            }
        }
        _audioClient.Stop();
    }

    public void Stop() {
        _stop = true;
        _eventHandle?.Set(); // 唤醒等待中的线程稈
        _captureThread?.Join(2000);
    }

    public void Dispose() {
        Stop();
        _eventHandle?.Dispose();
        _audioClient?.Dispose();
        _device?.Dispose();
    }
}

// ========== 音频捕获 ==========
public class AudioCaptureService {
    private LowLatencyLoopbackCapture? cap;
    private NetworkServer? srv;
    private EncodingType encodingType = EncodingType.Pcm;
    private int sampleRate = 48000;
    private int channels = 2;
    private int opusBitrate = 64; // kbps
    private IOpusEncoder? opusEncoder;
    private List<short> opusBuffer = new();
    // 保护 opusEncoder/opusBuffer 跨线程访问（TCP 控制线程 vs WASAPI 捕获线程）
    private readonly object audioLock = new();
    public event Action<string>? OnLog;
    public void SetServer(NetworkServer s) => srv = s;
    public string CurrentEncoding => encodingType.ToString();
    public int CurrentBitrate => opusBitrate;
    public void SetEncodingAndBitrate(EncodingType enc, int bitrate) {
        opusBitrate = bitrate;
        lock (audioLock) {
            if (encodingType == enc) {
                // 编码没变，只更新码率
                if (enc == EncodingType.Opus && cap != null) {
                    var newEnc = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                    newEnc.Bitrate = opusBitrate * 1000;
                    var old = opusEncoder;
                    opusEncoder = newEnc;
                    old?.Dispose();
                }
                return;
            }
            // 热切换编码：不停止捕获设备，只替换编码器（锁内替换+Dispose，与 FlushOpus 互斥）
            opusBuffer.Clear();
            if (enc == EncodingType.Opus) {
                var newEnc = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                newEnc.Bitrate = opusBitrate * 1000;
                var old = opusEncoder;
                opusEncoder = newEnc;
                old?.Dispose();
            } else {
                var old = opusEncoder;
                opusEncoder = null;
                old?.Dispose();
            }
            encodingType = enc;
        }
        OnLog?.Invoke($"编码已切换: {enc} {opusBitrate}kbps");
    }

    public void Start() {
        cap = new();
        cap.Start(); // 先启动以获取格式信息
        sampleRate = cap.WaveFormat.SampleRate;
        channels = cap.WaveFormat.Channels;
        OnLog?.Invoke($"WASAPI 格式: {sampleRate}Hz, {channels}ch, 32bit float (低延迟 20ms 缓冲)");

        if (encodingType == EncodingType.Opus) {
            lock (audioLock) {
                opusEncoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                opusEncoder.Bitrate = opusBitrate * 1000;
                opusBuffer.Clear();
            }
        }

        cap.DataAvailable += OnDataAvailable;
        _callbackCount2 = 0;
        OnLog?.Invoke($"音频捕获已启动 ({encodingType})");
    }

    private DateTime _lastCallbackTime;
    private int _callbackCount2;

    private void OnDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e) {
        if (srv?.Connected != true || e.BytesRecorded <= 0) return;

        // WASAPI 回调间隔测量
        var now2 = DateTime.UtcNow;
        if (_callbackCount2 > 0 && _callbackCount2 <= 5) {
            var interval = (now2 - _lastCallbackTime).TotalMilliseconds;
            var bufMs = e.BytesRecorded / (4.0 * channels) / sampleRate * 1000;
            OnLog?.Invoke($"[WASAPI] 回调#{_callbackCount2}: 间隔={interval:F1}ms, 缓冲={e.BytesRecorded}B={bufMs:F1}ms");
        }
        _lastCallbackTime = now2;
        _callbackCount2++;

        // 在最早处捕获时间戳，用于端到端延迟测量
        long captureTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        int srcChannels = channels;
        int srcFrames = e.BytesRecorded / (4 * srcChannels); // float32 per sample
        if (srcFrames <= 0) return;

        // 热路径：用 ArrayPool 复用缓冲，避免每 10ms 回调 3~4 次大数组分配
        int totalSamples = srcFrames * srcChannels;
        var floatBuf = ArrayPool<float>.Shared.Rent(totalSamples);
        var rawShort = ArrayPool<short>.Shared.Rent(totalSamples);
        try {
            Buffer.BlockCopy(e.Buffer, 0, floatBuf, 0, totalSamples * 4);

            // 原始格式 float→short（Opus 编码器内部处理重采样，无需预处理）
            for (int i = 0; i < totalSamples; i++) {
                float s = Math.Clamp(floatBuf[i], -1f, 1f);
                rawShort[i] = (short)(s * 32767f);
            }

            if (encodingType == EncodingType.Opus) {
                lock (audioLock) {
                    opusBuffer.AddRange(rawShort.AsSpan(0, totalSamples));
                }
                FlushOpus(captureTime);
            } else {
                const int outRate = 48000;
                float[] stereo48;
                int validLen;
                if (sampleRate == outRate && srcChannels == 2) {
                    // 直通分支：floatBuf 是池化数组，有效长度是 totalSamples（不能依赖数组 Length）
                    stereo48 = floatBuf;
                    validLen = totalSamples;
                } else {
                    stereo48 = ResampleToStereo48(floatBuf, sampleRate, outRate, srcFrames, srcChannels);
                    validLen = stereo48.Length;
                }
                var shortBuf = ArrayPool<short>.Shared.Rent(validLen);
                try {
                    for (int i = 0; i < validLen; i++) {
                        float s = Math.Clamp(stereo48[i], -1f, 1f);
                        shortBuf[i] = (short)(s * 32767f);
                    }
                    long encodeTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (encodingType == EncodingType.Adpcm) {
                        byte[] adpcm = AdpcmCodec.Encode(shortBuf, 2, validLen);
                        _ = srv.SendAudioAsync(adpcm, outRate, 2, EncodingType.Adpcm, captureTime, encodeTime);
                    } else {
                        var pcm = new byte[validLen * 2];
                        Buffer.BlockCopy(shortBuf, 0, pcm, 0, pcm.Length);
                        _ = srv.SendAudioAsync(pcm, outRate, 2, EncodingType.Pcm, captureTime, encodeTime);
                    }
                } finally {
                    ArrayPool<short>.Shared.Return(shortBuf);
                }
            }
        } finally {
            ArrayPool<float>.Shared.Return(floatBuf);
            ArrayPool<short>.Shared.Return(rawShort);
        }
    }

    /// <summary>
    /// 将任意采样率/声道的 float 音频重采样为 48kHz 立体声
    /// </summary>
    public static float[] ResampleToStereo48(float[] input, int srcRate, int dstRate, int srcFrames, int srcChannels) {
        int dstFrames = (int)((long)srcFrames * dstRate / srcRate);
        if (dstFrames <= 0) return Array.Empty<float>();
        var output = new float[dstFrames * 2];
        float ratio = (float)srcRate / dstRate;

        for (int i = 0; i < dstFrames; i++) {
            float srcPos = i * ratio;
            int srcIdx = (int)srcPos;
            float frac = srcPos - srcIdx;
            int srcIdx1 = Math.Min(srcIdx + 1, srcFrames - 1);

            // 取左右声道（环绕声取前两个声道，单声道复制）
            float left0, right0, left1, right1;
            if (srcChannels >= 2) {
                left0 = input[srcIdx * srcChannels];
                right0 = input[srcIdx * srcChannels + 1];
                left1 = input[srcIdx1 * srcChannels];
                right1 = input[srcIdx1 * srcChannels + 1];
            } else {
                left0 = right0 = input[srcIdx];
                left1 = right1 = input[srcIdx1];
            }

            output[i * 2] = left0 * (1 - frac) + left1 * frac;
            output[i * 2 + 1] = right0 * (1 - frac) + right1 * frac;
        }
        return output;
    }
    private void FlushOpus(long captureTime) {
        int frameSamples = 960; // 20ms @ 48kHz
        int frameTotal = frameSamples * channels;
        // 整个编码循环持锁：防止 SetEncodingAndBitrate 并发 Dispose 正在使用的编码器
        lock (audioLock) {
            var encoder = opusEncoder;
            if (encoder == null) return;
            while (opusBuffer.Count >= frameTotal) {
                var frame = opusBuffer.GetRange(0, frameTotal).ToArray();
                opusBuffer.RemoveRange(0, frameTotal);
                byte[] outBuf = new byte[4000];
                // Span 重载：避免过时数组 API（Concentus 2.2.2 仅提供 Span 接口）
                int encoded = encoder.Encode(frame.AsSpan(), frameSamples, outBuf.AsSpan(), outBuf.Length);
                if (encoded > 0) {
                    long encodeTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var opus = new byte[encoded];
                    Array.Copy(outBuf, opus, encoded);
                    _ = srv.SendAudioAsync(opus, sampleRate, (byte)channels, EncodingType.Opus, captureTime, encodeTime);
                }
            }
        }
    }

    public void Stop() {
        if (cap != null) {
            cap.DataAvailable -= OnDataAvailable;
            cap.Stop(); cap.Dispose(); cap = null;
        }
        lock (audioLock) {
            opusEncoder?.Dispose();
            opusEncoder = null;
            opusBuffer.Clear();
        }
        OnLog?.Invoke("音频捕获已停止");
    }
}

// 音频播放
public class AudioPlaybackService {
    private WaveOutEvent? wav; private BufferedWaveProvider? prov;
    private readonly object playLock = new(); // 保护 wav/prov：UDP 接收线程(WriteData) vs UI 线程(Start/Stop)
    private int _deviceNumber = -1;
    public int BufferDurationMs { get; set; } = 200;
    public bool IsPlaying { get { lock (playLock) return wav != null; } }
    public int DeviceNumber { get => _deviceNumber; set => _deviceNumber = value; }
    public event Action<string>? OnLog;

    public static string[] GetDeviceNames() {
        int count = WaveOut.DeviceCount;
        var names = new string[count];
        for (int i = 0; i < count; i++) {
            var caps = WaveOut.GetCapabilities(i);
            names[i] = caps.ProductName;
        }
        return names;
    }

    public void Start() {
        lock (playLock) {
            var fmt = new WaveFormat(48000, 16, 2);
            prov = new(fmt) { BufferDuration = TimeSpan.FromMilliseconds(Math.Max(BufferDurationMs, 50)), DiscardOnBufferOverflow = true };
            int devNum = _deviceNumber;
            if (devNum < 0 || devNum >= WaveOut.DeviceCount) devNum = 0;
            wav = new() { DeviceNumber = devNum };
            wav.Init(prov); wav.Play();
            string devName = devNum < WaveOut.DeviceCount ? WaveOut.GetCapabilities(devNum).ProductName : "Default";
            OnLog?.Invoke($"音频播放已启动 (设备: {devName}, 缓冲 {BufferDurationMs}ms)");
        }
    }
    public void RestartWithNewBuffer() {
        // lock 可重入：内部调 Stop/Start 安全
        lock (playLock) {
            bool wasPlaying = wav != null;
            Stop();
            if (wasPlaying) Start();
        }
    }
    public void WriteData(byte[] d) {
        lock (playLock) prov?.AddSamples(d, 0, d.Length);
    }
    public void Stop() {
        lock (playLock) {
            wav?.Stop(); wav?.Dispose(); wav = null; prov = null;
            OnLog?.Invoke("音频播放已停止");
        }
    }
}

// 数据包协议
