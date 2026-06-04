using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Concentus.Structs;
using Concentus.Enums;

namespace AudioRelayWinUI;

static class Program
{
    [STAThread]
    static void Main() => Application.Run(new MainForm());
}

public class MainForm : Form
{
    // 控件
    private TextBox txtPort = new() { Text = "9287" };
    private Button btnStart = new() { Text = "▶ 启动服务" };
    private Label lblStatus = new() { Text = "● 未启动", ForeColor = Color.Red };
    private ToggleButton btnPcToPhone = new("⬇ PC → 手机");
    private ToggleButton btnPhoneToPc = new("⬆ 手机 → PC");
private TextBox txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, ForeColor = Color.FromArgb(0, 100, 50), Font = new("Consolas", 9) };

    // 服务
    private NetworkServer server = new();
    private AudioCaptureService capture = new();
    private AudioPlaybackService playback = new();
    private CancellationTokenSource? cts;

    public MainForm()
    {
        // === 深色音频工作室主题 ===
        this.Text = "AudioRelay - 鸿蒙 ↔ Windows";
        this.Size = new Size(620, 600);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.BackColor = Color.FromArgb(240, 240, 245);
        this.Font = new Font("Microsoft YaHei UI", 10);

        var title = new Label { Text = "AudioRelay",
            Font = new Font("Microsoft YaHei UI", 24, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 99, 235),
            Location = new Point(22, 18), Size = new Size(300, 42) };
        var sub = new Label { Text = "鸿蒙 ⇄ Windows 音频串流",
            Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(80, 80, 100),
            Location = new Point(24, 58), Size = new Size(300, 22) };

        var g1 = new GroupBox { Text = " 服务设置 ",
            Location = new Point(22, 95), Size = new Size(575, 110),
            BackColor = Color.White, ForeColor = Color.FromArgb(50, 50, 70) };

        txtPort = new TextBox { Text = "9287",
            BackColor = Color.White, ForeColor = Color.FromArgb(40, 40, 60),
            Location = new Point(300, 28), Size = new Size(80, 25),
            BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10) };
        btnStart = new Button { Text = "▶ 启动服务",
            Location = new Point(15, 65), Size = new Size(545, 32),
            FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 },
            BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White };
        btnStart.Click += OnStartClick;
        g1.Controls.AddRange([new Label { Text = "端口:", Location = new Point(15, 30),
            Size = new Size(40, 22), ForeColor = Color.FromArgb(80, 80, 100) },
            txtPort, btnStart]);

        lblStatus = new Label { Text = "● 未启动",
            ForeColor = Color.FromArgb(255, 71, 87),
            Location = new Point(22, 215), Size = new Size(575, 25),
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };

        var g2 = new GroupBox { Text = " 音频流控制 ",
            Location = new Point(22, 248), Size = new Size(575, 145),
            BackColor = Color.White, ForeColor = Color.FromArgb(50, 50, 70) };

        btnPcToPhone = new ToggleButton("⬇ PC → 手机") {
            Location = new Point(15, 50), Size = new Size(260, 70),
            Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), Enabled = false };
        btnPcToPhone.Click += OnPcToPhoneClick;
        btnPhoneToPc = new ToggleButton("⬆ 手机 → PC") {
            Location = new Point(295, 50), Size = new Size(260, 70),
            Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), Enabled = false };
        btnPhoneToPc.Click += OnPhoneToPcClick;
        g2.Controls.AddRange([
            btnPcToPhone, btnPhoneToPc]);

        txtLog = new TextBox { Location = new Point(22, 410), Size = new Size(575, 150),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White, ForeColor = Color.FromArgb(37, 99, 235),
            Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None };

        Controls.AddRange([title, sub, g1, lblStatus, g2, txtLog]);

        // 服务回调
        server.OnLog += Log;
        capture.OnLog += Log;
        playback.OnLog += Log;
        // 服务回调
        server.OnLog += Log;
        capture.OnLog += Log;
        playback.OnLog += Log;
        server.OnConnected += (ok) => Invoke(() => {
            if (ok) {
                lblStatus.Text = "● 已连接"; lblStatus.ForeColor = Color.Green;
                btnPcToPhone.Enabled = true; btnPhoneToPc.Enabled = true;
                Log("鸿蒙端已连接");
            } else {
                lblStatus.Text = "● 等待连接..."; lblStatus.ForeColor = Color.Orange;
                btnPcToPhone.Enabled = false; btnPcToPhone.Off();
                btnPhoneToPc.Enabled = false; btnPhoneToPc.Off();
                capture.Stop(); playback.Stop();
                Log("鸿蒙端已断开");
            }
        });
        // 收到手机端音频数据
        server.OnAudioData += (pkt) => {
            if (pkt.Direction != StreamDirection.PhoneToPc) return;
            if (pkt.Encoding == EncodingType.Pcm) {
                playback.WriteData(pkt.Payload);
            } else if (pkt.Encoding == EncodingType.Adpcm) {
                byte[] pcm = AdpcmCodec.Decode(pkt.Payload, pkt.Channels, pkt.SampleRate);
                playback.WriteData(pcm);
            } else {
                // Opus 暂未实现手机端编码，按 PCM 处理
                playback.WriteData(pkt.Payload);
            }
        };
        capture.SetServer(server);
    }

    private async void OnStartClick(object? s, EventArgs e)
    {
        if (cts != null) {
            cts.Cancel(); cts = null;
            capture.Stop(); playback.Stop();
            btnStart.Text = "▶ 启动服务"; btnStart.BackColor = Color.FromArgb(37, 99, 235);
            txtPort.ReadOnly = false; txtPort.BackColor = Color.White;
            lblStatus.Text = "● 已停止"; lblStatus.ForeColor = Color.Red;
            btnPcToPhone.Enabled = false; btnPhoneToPc.Enabled = false;
            Log("服务已停止"); return;
        }
        cts = new();
        btnStart.Text = "启动中..."; btnStart.Enabled = false;
        try {
            _ = server.StartAsync(int.Parse(txtPort.Text), cts.Token);
            await Task.Delay(300);
            btnStart.Text = "■ 停止服务"; btnStart.BackColor = Color.FromArgb(239, 68, 68);
            txtPort.ReadOnly = true; txtPort.BackColor = Color.LightGray;
            lblStatus.Text = "● 等待连接..."; lblStatus.ForeColor = Color.Orange;
            Log($"服务已启动，端口 {txtPort.Text}");
        } catch (Exception ex) { Log($"失败: {ex.Message}"); }
        finally { btnStart.Enabled = true; }
    }

    private void OnPcToPhoneClick(object? s, EventArgs e) {
        if (btnPcToPhone.Active) { capture.Stop(); btnPcToPhone.Off(); Log("■ PC→手机 已关闭"); return; }
        capture.SetEncoding(EncodingType.Opus);
        capture.SetBitrate(64);
        capture.Start();
        btnPcToPhone.On();
        Log("▶ PC→手机 已开启 (Opus 64kbps)");
    }


    private void OnPhoneToPcClick(object? s, EventArgs e) {
        if (btnPhoneToPc.Active) { playback.Stop(); btnPhoneToPc.Off(); Log("■ 手机→PC 已关闭"); }
        else { playback.BufferDurationMs = 200; playback.Start(); btnPhoneToPc.On(); Log("▶ 手机→PC 已开启（缓冲 200ms）"); }
    }

    private void Log(string msg) => Invoke(() => {
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        txtLog.SelectionStart = txtLog.Text.Length; txtLog.ScrollToCaret();
    });
    protected override void OnFormClosing(FormClosingEventArgs e) {
        cts?.Cancel(); capture.Stop(); playback.Stop();
        base.OnFormClosing(e);
    }
}

// 网络服务
public class NetworkServer {
    private TcpListener? listener; private TcpClient? client; private NetworkStream? stream;
    private int seq; public bool Connected => client?.Connected ?? false;
    public event Action<AudioPacket>? OnAudioData;
    public event Action<bool>? OnConnected;
    public event Action<string>? OnLog;

    public async Task StartAsync(int port, CancellationToken tk) {
        listener = new(IPAddress.Any, port); listener.Start();
        OnLog?.Invoke($"监听端口 {port}");
        while (!tk.IsCancellationRequested) {
            try {
                client = await listener.AcceptTcpClientAsync(tk);
                client.NoDelay = true;
                stream = client.GetStream();
                OnConnected?.Invoke(true);
                var buf = new byte[8192];
                while (client.Connected && !tk.IsCancellationRequested) {
                    int n = await stream.ReadAsync(buf, 0, buf.Length, tk);
                    if (n == 0) break;
                    var d = new byte[n]; Array.Copy(buf, d, n);
                    var pkt = AudioPacket.Deserialize(d);
                    if (pkt.MsgType == MessageType.AudioData) OnAudioData?.Invoke(pkt);
                }
            } catch (OperationCanceledException) { break; }
            catch (Exception ex) { OnLog?.Invoke($"连接异常: {ex.Message}"); }
            finally { OnConnected?.Invoke(false); stream?.Close(); client?.Close(); }
        }
    }
    public async Task SendAudioAsync(byte[] data, int sr, byte ch, EncodingType enc = EncodingType.Pcm) {
        if (stream == null) return;
        var p = new AudioPacket { MsgType = MessageType.AudioData, Direction = StreamDirection.PcToPhone,
            Encoding = enc, Sequence = seq++,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SampleRate = sr, Channels = ch, BitsPerSample = 16, Payload = data };
        await stream.WriteAsync(p.Serialize());
    }
    public void Stop() { stream?.Close(); client?.Close(); listener?.Stop(); }
}

// ========== ADPCM 编解码器 ==========
public static class AdpcmCodec
{
    private static readonly int[] StepTable = {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
        253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
        3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    };

    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    /// <summary>编码 PCM short[] 为 ADPCM byte[]（每声道独立编码，4-bit 交错打包）</summary>
    public static byte[] Encode(short[] pcm, int channels)
    {
        int totalSamples = pcm.Length;
        int encodedLen = (totalSamples + 1) / 2; // 每2个样本→1字节
        byte[] output = new byte[encodedLen + channels * 8]; // 前 ch*8 字节存初始状态

        // 保存每个声道的初始预测值和步长索引
        int[] predictors = new int[channels];
        int[] indices = new int[channels];
        for (int ch = 0; ch < channels; ch++)
        {
            indices[ch] = 0;
            predictors[ch] = 0;
        }

        int sampleIdx = 0;
        int outIdx = channels * 8;
        int frames = totalSamples / channels;

        for (int f = 0; f < frames; f++)
        {
            for (int ch = 0; ch < channels; ch += 2)
            {
                int code0 = EncodeSample(pcm[sampleIdx], ref predictors[ch], ref indices[ch]);
                int code1 = (ch + 1 < channels)
                    ? EncodeSample(pcm[sampleIdx + 1], ref predictors[ch + 1], ref indices[ch + 1])
                    : 0;
                output[outIdx++] = (byte)(code0 | (code1 << 4));
                sampleIdx += Math.Min(2, channels);
            }
        }

        // 把预测器和步长索引存到开头
        for (int ch = 0; ch < channels; ch++)
        {
            BitConverter.GetBytes(predictors[ch]).CopyTo(output, ch * 4);
            BitConverter.GetBytes(indices[ch]).CopyTo(output, channels * 4 + ch * 4);
        }

        return output;
    }

    private static int EncodeSample(int sample, ref int predictor, ref int index)
    {
        int diff = sample - predictor;
        int code = 0;
        int step = StepTable[Math.Clamp(index, 0, 88)];

        int absDiff = Math.Abs(diff);
        if (diff < 0) code = 8;

        int delta = step >> 3;
        int tmp = step;
        if (absDiff >= tmp) { code |= 4; absDiff -= tmp; delta += tmp; }
        tmp >>= 1;
        if (absDiff >= tmp) { code |= 2; absDiff -= tmp; delta += tmp; }
        tmp >>= 1;
        if (absDiff >= tmp) { code |= 1; delta += tmp; }

        if ((code & 8) != 0)
            predictor -= delta;
        else
            predictor += delta;

        predictor = Math.Clamp(predictor, -32768, 32767);
        index = Math.Clamp(index + IndexTable[code & 7], 0, 88);

        return code & 0x0F;
    }

    /// <summary>解码 ADPCM byte[] 为 PCM short[]</summary>
    public static byte[] Decode(byte[] adpcm, byte channels, int sampleRate)
    {
        // 从开头读取初始状态
        int[] predictors = new int[channels];
        int[] indices = new int[channels];
        for (int ch = 0; ch < channels; ch++)
            predictors[ch] = BitConverter.ToInt32(adpcm, ch * 4);
        for (int ch = 0; ch < channels; ch++)
            indices[ch] = BitConverter.ToInt32(adpcm, channels * 4 + ch * 4);

        int dataOffset = channels * 8;
        int dataLen = adpcm.Length - dataOffset;
        int sampleCount = dataLen * 2; // 每字节2个样本
        short[] output = new short[sampleCount];

        int inIdx = dataOffset;
        int outIdx = 0;

        while (inIdx < adpcm.Length)
        {
            for (int ch = 0; ch < channels; ch += 2)
            {
                byte b = adpcm[inIdx++];
                int code0 = b & 0x0F;
                int code1 = (b >> 4) & 0x0F;

                output[outIdx++] = DecodeSample(code0, ref predictors[ch], ref indices[ch]);
                if (ch + 1 < channels)
                    output[outIdx++] = DecodeSample(code1, ref predictors[ch + 1], ref indices[ch + 1]);
            }
        }

        // 转 byte[]
        byte[] pcmBytes = new byte[output.Length * 2];
        Buffer.BlockCopy(output, 0, pcmBytes, 0, pcmBytes.Length);
        return pcmBytes;
    }

    private static short DecodeSample(int code, ref int predictor, ref int index)
    {
        int step = StepTable[Math.Clamp(index, 0, 88)];
        int delta = step >> 3;

        if ((code & 4) != 0) delta += step;
        if ((code & 2) != 0) delta += step >> 1;
        if ((code & 1) != 0) delta += step >> 2;

        if ((code & 8) != 0)
            predictor -= delta;
        else
            predictor += delta;

        predictor = Math.Clamp(predictor, -32768, 32767);
        index = Math.Clamp(index + IndexTable[code & 7], 0, 88);

        return (short)predictor;
    }
}

// ========== 音频捕获 ==========
public class AudioCaptureService {
    private WasapiLoopbackCapture? cap;
    private NetworkServer? srv;
    private EncodingType encodingType = EncodingType.Opus;
    private int sampleRate = 48000;
    private int channels = 2;
    private int opusBitrate = 64; // kbps
    private OpusEncoder? opusEncoder;
    private List<short> opusBuffer = new();
    public event Action<string>? OnLog;
    public void SetServer(NetworkServer s) => srv = s;
    public void SetEncoding(EncodingType enc) { encodingType = enc; }
    public void SetBitrate(int br) { opusBitrate = br; }

    public void Start() {
        cap = new();
        sampleRate = cap.WaveFormat.SampleRate;
        channels = cap.WaveFormat.Channels;

        if (encodingType == EncodingType.Opus) {
            opusEncoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            opusEncoder.Bitrate = opusBitrate * 1000;
            opusBuffer.Clear();
        }

        cap.DataAvailable += OnDataAvailable;
        cap.StartRecording();
        OnLog?.Invoke($"音频捕获已启动 ({encodingType})");
    }

    private void OnDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e) {
        if (srv?.Connected != true || e.BytesRecorded <= 0) return;

        int sampleCount = e.BytesRecorded / 4;
        var floatBuf = new float[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, floatBuf, 0, e.BytesRecorded);

        var shortBuf = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++) {
            float s = Math.Clamp(floatBuf[i], -1f, 1f);
            shortBuf[i] = (short)(s * 32767f);
        }

        if (encodingType == EncodingType.Opus) {
            lock (opusBuffer) {
                opusBuffer.AddRange(shortBuf);
            }
            FlushOpus();
        } else if (encodingType == EncodingType.Adpcm) {
            byte[] adpcm = AdpcmCodec.Encode(shortBuf, channels);
            _ = srv.SendAudioAsync(adpcm, sampleRate, (byte)channels, EncodingType.Adpcm);
        } else {
            var pcm = new byte[sampleCount * 2];
            Buffer.BlockCopy(shortBuf, 0, pcm, 0, pcm.Length);
            _ = srv.SendAudioAsync(pcm, sampleRate, (byte)channels, EncodingType.Pcm);
        }
    }

    private void FlushOpus() {
        int frameSamples = 960; // 20ms @ 48kHz
        int frameTotal = frameSamples * channels;
        lock (opusBuffer) {
            while (opusBuffer.Count >= frameTotal) {
                var frame = opusBuffer.GetRange(0, frameTotal).ToArray();
                opusBuffer.RemoveRange(0, frameTotal);
                byte[] outBuf = new byte[4000];
                int encoded = opusEncoder!.Encode(frame, 0, frameSamples, outBuf, 0, outBuf.Length);
                if (encoded > 0) {
                    var opus = new byte[encoded];
                    Array.Copy(outBuf, opus, encoded);
                    _ = srv.SendAudioAsync(opus, sampleRate, (byte)channels, EncodingType.Opus);
                }
            }
        }
    }

    public void Stop() {
        if (cap != null) {
            cap.DataAvailable -= OnDataAvailable;
            cap.StopRecording(); cap.Dispose(); cap = null;
        }
        opusEncoder?.Dispose();
        opusEncoder = null;
        lock (opusBuffer) { opusBuffer.Clear(); }
        OnLog?.Invoke("音频捕获已停止");
    }
}

// 音频播放
public class AudioPlaybackService {
    private WaveOutEvent? wav; private BufferedWaveProvider? prov;
    public int BufferDurationMs { get; set; } = 2000;
    public event Action<string>? OnLog;
    public void Start() {
        var fmt = new WaveFormat(48000, 16, 2); // 16-bit PCM 匹配 HMOS 麦克风
        prov = new(fmt) { BufferDuration = TimeSpan.FromMilliseconds(BufferDurationMs), DiscardOnBufferOverflow = true };
        wav = new(); wav.Init(prov); wav.Play();
        OnLog?.Invoke($"音频播放已启动 (缓冲 {BufferDurationMs}ms)");
    }
    public void WriteData(byte[] d) => prov?.AddSamples(d, 0, d.Length);
    public void Stop() { wav?.Stop(); wav?.Dispose(); wav = null; prov = null; OnLog?.Invoke("音频播放已停止"); }
}

// 数据包协议
public enum MessageType : byte { Control = 0, AudioData = 1 }
public enum ControlCommand : byte { StartStream = 3, StopStream = 4 }
public enum StreamDirection : byte { PcToPhone = 0, PhoneToPc = 1 }
public enum EncodingType : byte { Pcm = 0, Opus = 1, Adpcm = 2 }
public class AudioPacket {
    public MessageType MsgType; public ControlCommand? Command; public StreamDirection? Direction;
    public EncodingType Encoding;
    public int Sequence; public long Timestamp; public int SampleRate;
    public byte Channels, BitsPerSample; public byte[] Payload = [];

    public byte[] Serialize() {
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write((byte)MsgType); bw.Write((byte?)(byte?)Command ?? 0xFF);
        bw.Write((byte?)(byte?)Direction ?? 0xFF); bw.Write((byte)Encoding);
        bw.Write(Sequence); bw.Write(Timestamp);
        bw.Write(SampleRate); bw.Write(Channels); bw.Write(BitsPerSample); bw.Write((ushort)0);
        bw.Write(Payload.Length); if (Payload.Length > 0) bw.Write(Payload);
        return ms.ToArray();
    }
    public static AudioPacket Deserialize(byte[] d) {
        using var ms = new MemoryStream(d); using var br = new BinaryReader(ms);
        var p = new AudioPacket { MsgType = (MessageType)br.ReadByte() };
        var c = br.ReadByte(); if (c != 0xFF) p.Command = (ControlCommand)c;
        var dr = br.ReadByte(); if (dr != 0xFF) p.Direction = (StreamDirection)dr;
        p.Encoding = (EncodingType)br.ReadByte();
        p.Sequence = br.ReadInt32(); p.Timestamp = br.ReadInt64();
        p.SampleRate = br.ReadInt32(); p.Channels = br.ReadByte(); p.BitsPerSample = br.ReadByte(); br.ReadUInt16();
        int len = br.ReadInt32(); if (len > 0) p.Payload = br.ReadBytes(len);
        return p;
    }
}

// 开关按钮
public class ToggleButton : Button {
    private bool active;
    public bool Active => active;
    private readonly string offText;
    public ToggleButton(string text) : base() {
        offText = text; Text = text; FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 2; FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        BackColor = Color.White; ForeColor = Color.FromArgb(100, 100, 100);
        Font = new("微软雅黑", 11, FontStyle.Bold);
    }
    public void On() { active = true; BackColor = Color.FromArgb(16, 185, 129); ForeColor = Color.White;
        FlatAppearance.BorderColor = Color.FromArgb(16, 185, 129); Text = "⏹ " + offText[2..]; }
    public void Off() { active = false; BackColor = Color.White; ForeColor = Color.FromArgb(100, 100, 100);
        FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200); Text = offText; }
}
