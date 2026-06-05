// 🤖 AI 辅助生成 — Claude (Anthropic)
// 项目: AudioRelayHM 鸿蒙↔Windows 音频串流

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
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
    private TextBox txtPort;
    private Button btnStart;
    private Label lblStatus;
    private ToggleButton btnPcToPhone;
    private ToggleButton btnPhoneToPc;
    private TextBox txtLog;

    // 服务
    private NetworkServer server = new();
    private AudioCaptureService capture = new();
    private AudioPlaybackService playback = new();
    private CancellationTokenSource? cts;

    public MainForm()
    {
        var BG = Color.FromArgb(15, 23, 42);
        var CARD = Color.FromArgb(30, 41, 59);
        var BORDER = Color.FromArgb(51, 65, 85);
        var ACCENT = Color.FromArgb(59, 130, 246);
        var SUCCESS = Color.FromArgb(16, 185, 129);
        var DANGER = Color.FromArgb(239, 68, 68);
        var WARN = Color.FromArgb(245, 158, 11);
        var TXT = Color.FromArgb(241, 245, 249);
        var TXT2 = Color.FromArgb(148, 163, 184);
        var TXT3 = Color.FromArgb(100, 116, 139);
        var INPUT = Color.FromArgb(15, 23, 42);
        var LOG_BG = Color.FromArgb(2, 6, 23);

        this.Text = "AudioRelay";
        this.Size = new Size(660, 720);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.BackColor = BG;
        this.Font = new Font("Segoe UI", 10);

        int x = 24, w = 596;

        // === Title ===
        var title = new Label { Text = "AudioRelay",
            Font = new Font("Segoe UI", 24, FontStyle.Bold), ForeColor = ACCENT,
            Location = new Point(x, 16), AutoSize = true };
        var sub = new Label { Text = "鸿蒙 \u21C4 Windows 音频串流",
            Font = new Font("Microsoft YaHei UI", 10), ForeColor = TXT2,
            Location = new Point(x + 2, 52), AutoSize = true };

        // === Service Settings Card ===
        var svcCard = new RoundedPanel {
            Location = new Point(x, 88), Size = new Size(w, 105),
            BackColor = CARD, BorderColor = BORDER, CornerRadius = 12 };

        var portLbl = new Label { Text = "PORT",
            Font = new Font("Segoe UI", 8), ForeColor = TXT3,
            Location = new Point(18, 14), AutoSize = true };
        txtPort = new TextBox { Text = "9287",
            BackColor = INPUT, ForeColor = TXT, BorderStyle = BorderStyle.None,
            Location = new Point(18, 36), Size = new Size(80, 28),
            Font = new Font("Consolas", 12), TextAlign = HorizontalAlignment.Center };
        btnStart = new Button { Text = "\u25B6  启动服务",
            Location = new Point(115, 28), Size = new Size(w - 135, 40),
            FlatStyle = FlatStyle.Flat, BackColor = ACCENT, ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), Cursor = Cursors.Hand };
        btnStart.FlatAppearance.BorderSize = 0;
        btnStart.Click += OnStartClick;
        svcCard.Controls.AddRange([portLbl, txtPort, btnStart]);

        // === Status ===
        lblStatus = new Label { Text = "\u25CF  未启动",
            ForeColor = DANGER, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
            Location = new Point(x, 206), Size = new Size(w, 24) };

        // === Stream Control Card ===
        var streamCard = new RoundedPanel {
            Location = new Point(x, 242), Size = new Size(w, 240),
            BackColor = CARD, BorderColor = BORDER, CornerRadius = 12 };
        var streamTitle = new Label { Text = "STREAM CONTROL",
            Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = TXT3,
            Location = new Point(18, 14), AutoSize = true };

        btnPcToPhone = new ToggleButton("\U0001F4BB", "PC \u2192 手机", "将电脑声音传到手机播放") {
            Location = new Point(16, 42), Size = new Size(w - 32, 82), Enabled = false };
        btnPcToPhone.Click += OnPcToPhoneClick;

        btnPhoneToPc = new ToggleButton("\U0001F4F1", "手机 \u2192 PC", "将手机麦克风声音传到电脑") {
            Location = new Point(16, 136), Size = new Size(w - 32, 82), Enabled = false };
        btnPhoneToPc.Click += OnPhoneToPcClick;

        streamCard.Controls.AddRange([streamTitle, btnPcToPhone, btnPhoneToPc]);

        // === Log Card ===
        var logCard = new RoundedPanel {
            Location = new Point(x, 496), Size = new Size(w, 180),
            BackColor = CARD, BorderColor = BORDER, CornerRadius = 12 };
        var logTitle = new Label { Text = "LOG",
            Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = TXT3,
            Location = new Point(18, 10), AutoSize = true };
        txtLog = new TextBox {
            Location = new Point(12, 32), Size = new Size(w - 24, 136),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = LOG_BG, ForeColor = Color.FromArgb(74, 222, 128),
            Font = new Font("Cascadia Code", 9), BorderStyle = BorderStyle.None };
        logCard.Controls.AddRange([logTitle, txtLog]);

        Controls.AddRange([title, sub, svcCard, lblStatus, streamCard, logCard]);

        // === 服务回调 ===
        server.OnLog += Log;
        capture.OnLog += Log;
        playback.OnLog += Log;
        server.OnConnected += (ok) => Invoke(() => {
            if (ok) {
                lblStatus.Text = "\u25CF  已连接"; lblStatus.ForeColor = SUCCESS;
                btnPcToPhone.Enabled = true; btnPhoneToPc.Enabled = true;
                Log("鸿蒙端已连接");
            } else {
                lblStatus.Text = "\u25CF  等待连接..."; lblStatus.ForeColor = WARN;
                btnPcToPhone.Enabled = false; btnPcToPhone.SetActive(false, "\U0001F4BB  PC \u2192 手机");
                btnPhoneToPc.Enabled = false; btnPhoneToPc.SetActive(false, "\U0001F4F1  手机 \u2192 PC");
                capture.Stop(); playback.Stop();
                Log("鸿蒙端已断开");
            }
        });
        server.OnAudioData += (pkt) => {
            if (pkt.Direction != StreamDirection.PhoneToPc) return;
            if (pkt.Encoding == EncodingType.Pcm) {
                playback.WriteData(pkt.Payload);
            } else if (pkt.Encoding == EncodingType.Adpcm) {
                byte[] pcm = AdpcmCodec.Decode(pkt.Payload, pkt.Channels, pkt.SampleRate);
                playback.WriteData(pcm);
            } else {
                playback.WriteData(pkt.Payload);
            }
        };
        capture.SetServer(server);
    }

    private async void OnStartClick(object? s, EventArgs e)
    {
        var BG = Color.FromArgb(15, 23, 42);
        var ACCENT = Color.FromArgb(59, 130, 246);
        var DANGER = Color.FromArgb(239, 68, 68);
        var WARN = Color.FromArgb(245, 158, 11);
        if (cts != null) {
            cts.Cancel(); cts = null;
            capture.Stop(); playback.Stop();
            btnStart.Text = "\u25B6  启动服务"; btnStart.BackColor = ACCENT;
            txtPort.ReadOnly = false; txtPort.BackColor = BG;
            lblStatus.Text = "\u25CF  已停止"; lblStatus.ForeColor = DANGER;
            btnPcToPhone.Enabled = false; btnPhoneToPc.Enabled = false;
            Log("服务已停止"); return;
        }
        cts = new();
        btnStart.Text = "启动中..."; btnStart.Enabled = false;
        try {
            _ = server.StartAsync(int.Parse(txtPort.Text), cts.Token);
            await Task.Delay(300);
            btnStart.Text = "\u25A0  停止服务"; btnStart.BackColor = DANGER;
            txtPort.ReadOnly = true; txtPort.BackColor = Color.FromArgb(30, 41, 59);
            lblStatus.Text = "\u25CF  等待连接..."; lblStatus.ForeColor = WARN;
            Log($"服务已启动，端口 {txtPort.Text}");
        } catch (Exception ex) { Log($"失败: {ex.Message}"); }
        finally { btnStart.Enabled = true; }
    }

    private void OnPcToPhoneClick(object? s, EventArgs e) {
        if (btnPcToPhone.Active) { capture.Stop(); btnPcToPhone.SetActive(false, "\U0001F4BB  PC \u2192 手机"); Log("\u25A0 PC\u2192手机 已关闭"); return; }
        capture.SetEncoding(EncodingType.Opus);
        capture.SetBitrate(64);
        capture.Start();
        btnPcToPhone.SetActive(true, "\u23F9  PC \u2192 手机");
        Log("\u25B6 PC\u2192手机 已开启 (Opus 64kbps)");
    }

    private void OnPhoneToPcClick(object? s, EventArgs e) {
        if (btnPhoneToPc.Active) { playback.Stop(); btnPhoneToPc.SetActive(false, "\U0001F4F1  手机 \u2192 PC"); Log("\u25A0 手机\u2192PC 已关闭"); }
        else { playback.BufferDurationMs = 200; playback.Start(); btnPhoneToPc.SetActive(true, "\u23F9  手机 \u2192 PC"); Log("\u25B6 手机\u2192PC 已开启（缓冲 200ms）"); }
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
                var recvBuf = new MemoryStream();
                var readBuf = new byte[8192];
                while (client.Connected && !tk.IsCancellationRequested) {
                    int n = await stream.ReadAsync(readBuf, 0, readBuf.Length, tk);
                    if (n == 0) break;
                    recvBuf.Write(readBuf, 0, n);
                    var data = recvBuf.ToArray();
                    int offset = 0;
                    while (offset + 28 <= data.Length) {
                        int payloadLen = BitConverter.ToInt32(data, offset + 24);
                        int totalLen = 28 + payloadLen;
                        if (offset + totalLen > data.Length) break;
                        var pktData = new byte[totalLen];
                        Array.Copy(data, offset, pktData, 0, totalLen);
                        offset += totalLen;
                        var pkt = AudioPacket.Deserialize(pktData);
                        if (pkt.MsgType == MessageType.AudioData) OnAudioData?.Invoke(pkt);
                    }
                    if (offset > 0) {
                        var remaining = new byte[data.Length - offset];
                        if (remaining.Length > 0) Array.Copy(data, offset, remaining, 0, remaining.Length);
                        recvBuf = new MemoryStream();
                        recvBuf.Write(remaining, 0, remaining.Length);
                    } else if (data.Length > 1024 * 1024) {
                        recvBuf = new MemoryStream();
                    }
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

// 圆角面板
public class RoundedPanel : Panel {
    public int CornerRadius { get; set; } = 12;
    public Color BorderColor { get; set; } = Color.FromArgb(51, 65, 85);
    public RoundedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = CreateRoundedRect(rect, CornerRadius);
        using var brush = new SolidBrush(BackColor);
        g.FillPath(brush, path);
        using var pen = new Pen(BorderColor, 1);
        g.DrawPath(pen, path);
    }
    private static GraphicsPath CreateRoundedRect(Rectangle r, int rad) {
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, rad * 2, rad * 2, 180, 90);
        path.AddArc(r.Right - rad * 2, r.Top, rad * 2, rad * 2, 270, 90);
        path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        path.AddArc(r.Left, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// 流控制按钮
public class ToggleButton : Control {
    private bool active;
    public bool Active => active;
    private readonly string icon;
    private string label;
    private readonly string desc;
    private static readonly Color BG = Color.FromArgb(30, 41, 59);
    private static readonly Color BORDER = Color.FromArgb(51, 65, 85);
    private static readonly Color ACTIVE_BG = Color.FromArgb(16, 185, 129);
    private static readonly Color TXT = Color.FromArgb(241, 245, 249);
    private static readonly Color TXT2 = Color.FromArgb(148, 163, 184);
    private static readonly Color DIM = Color.FromArgb(100, 116, 139);

    public ToggleButton(string icon, string label, string desc) {
        this.icon = icon; this.label = label; this.desc = desc;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Cursor = Cursors.Hand;
    }
    public void SetActive(bool on, string newLabel) {
        active = on; label = newLabel; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        int rad = 10;
        using var path = CreateRoundedRect(rect, rad);

        Color fill = !Enabled ? Color.FromArgb(20, 30, 48) : active ? ACTIVE_BG : BG;
        Color border = !Enabled ? Color.FromArgb(40, 50, 65) : active ? ACTIVE_BG : BORDER;
        Color textCol = !Enabled ? DIM : TXT;
        Color descCol = !Enabled ? Color.FromArgb(60, 70, 85) : active ? Color.FromArgb(209, 250, 229) : TXT2;

        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        using (var pen = new Pen(border, 1.5f)) g.DrawPath(pen, path);

        var iconFont = new Font("Segoe UI Emoji", 18);
        var titleFont = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
        var descFont = new Font("Microsoft YaHei UI", 9);

        g.DrawString(icon, iconFont, new SolidBrush(textCol), 18, (Height - 40) / 2f);
        g.DrawString(label, titleFont, new SolidBrush(textCol), 58, (Height - 44) / 2f - 2);
        g.DrawString(desc, descFont, new SolidBrush(descCol), 58, (Height - 44) / 2f + 22);
    }
    private static GraphicsPath CreateRoundedRect(Rectangle r, int rad) {
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, rad * 2, rad * 2, 180, 90);
        path.AddArc(r.Right - rad * 2, r.Top, rad * 2, rad * 2, 270, 90);
        path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        path.AddArc(r.Left, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
