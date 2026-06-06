// 🤖 AI 辅助生成 — Claude (Anthropic)
// 项目: AudioRelayHM 鸿蒙↔Windows 音频串流

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
    // === 布局 ===
    private Panel navPanel = new();
    private Panel contentPanel = new();
    private Panel pnlServer = new();
    private Panel pnlPlayer = new();
    private Panel pnlSettings = new();
    private NavButton btnNavServer = new("服务器");
    private NavButton btnNavPlayer = new("播放器");
    private NavButton btnNavSettings = new("设置");

    // === 服务器页控件 ===
    private Label lblStatusDot = new();
    private Label lblStatusText = new();
    private Label lblIpAddr = new();
    private Label lblCaptureInfo = new();
    private TextBox txtLog = new();
    private TextBox txtPortServer = new();
    private Button btnStartStop = new();
    private LatencyChartPanel latencyChart = new();

    // === 播放器页控件 ===
    private Label lblStreamDot = new();
    private Label lblStreamStatus = new();
    private Label lblEncoding = new();
    private Label lblBitrate = new();
    private Label lblBuffer = new();
    private Label lblConnectedDevice = new();

    // === 设置页控件 ===
    private TextBox txtPortSettings = new();
    private ComboBox cboEncoding = new();
    private ComboBox cboBitrate = new();
    private ComboBox cboBuffer = new();
    private ComboBox cboOutputDevice = new();

    // === 服务 ===
    private NetworkServer server = new();
    private AudioCaptureService capture = new();
    private AudioPlaybackService playback = new();
    private CancellationTokenSource? cts;

    public MainForm()
    {
        // === 整体框架 ===
        this.Text = "AudioRelay";
        this.Size = new Size(800, 560);
        this.MinimumSize = new Size(700, 480);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(245, 245, 245);
        this.Font = new Font("Microsoft YaHei UI", 9.5f);

        // === 左侧导航栏 ===
        navPanel.Dock = DockStyle.Left;
        navPanel.Width = 160;
        navPanel.BackColor = Color.White;
        var lblTitle = new Label { Text = "AudioRelay", Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 99, 235), Location = new Point(16, 20), AutoSize = true };
        navPanel.Controls.Add(lblTitle);
        btnNavServer.Location = new Point(0, 70); btnNavServer.Size = new Size(160, 44);
        btnNavPlayer.Location = new Point(0, 114); btnNavPlayer.Size = new Size(160, 44);
        btnNavSettings.Location = new Point(0, 158); btnNavSettings.Size = new Size(160, 44);
        btnNavServer.Click += (s, e) => SwitchPage(0);
        btnNavPlayer.Click += (s, e) => SwitchPage(1);
        btnNavSettings.Click += (s, e) => SwitchPage(2);
        navPanel.Controls.AddRange([btnNavServer, btnNavPlayer, btnNavSettings]);

        // === 右侧内容区 ===
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.Padding = new Padding(20, 16, 20, 16);
        pnlServer.Dock = DockStyle.Fill; pnlPlayer.Dock = DockStyle.Fill; pnlSettings.Dock = DockStyle.Fill;
        BuildServerPage(); BuildPlayerPage(); BuildSettingsPage();
        contentPanel.Controls.AddRange([pnlSettings, pnlPlayer, pnlServer]);
        Controls.Add(contentPanel);
        Controls.Add(navPanel);
        SwitchPage(0);

        // === 事件注册 ===
        server.OnLog += Log; capture.OnLog += Log; playback.OnLog += Log;
        server.OnConnected += (ok) => Invoke(() => {
            if (ok) {
                lblStatusDot.ForeColor = Color.FromArgb(16, 185, 129);
                lblStatusText.Text = "已连接"; lblStatusText.ForeColor = Color.FromArgb(16, 185, 129);
                lblStreamDot.ForeColor = Color.FromArgb(16, 185, 129);
                lblStreamStatus.Text = "PC → 手机 串流中"; lblStreamStatus.ForeColor = Color.FromArgb(16, 185, 129);
                lblConnectedDevice.Text = "鸿蒙设备已连接";
                capture.Start();
                playback.DeviceNumber = cboOutputDevice.SelectedIndex;
                playback.Start();
                Log($"鸿蒙端已连接，自动开启 PC→手机 ({capture.CurrentEncoding} {capture.CurrentBitrate}kbps)");
            } else {
                lblStatusDot.ForeColor = Color.FromArgb(245, 158, 11);
                lblStatusText.Text = "等待连接..."; lblStatusText.ForeColor = Color.FromArgb(245, 158, 11);
                lblStreamDot.ForeColor = Color.FromArgb(245, 158, 11);
                lblStreamStatus.Text = "等待连接..."; lblStreamStatus.ForeColor = Color.FromArgb(245, 158, 11);
                lblConnectedDevice.Text = "未连接任何设备";
                capture.Stop(); playback.Stop();
                latencyChart.Clear();
                Log("鸿蒙端已断开，串流已停止");
            }
        });
        server.OnAudioData += (pkt) => {
            if (pkt.Direction != StreamDirection.PhoneToPc) return;
            if (pkt.Encoding == EncodingType.Pcm) playback.WriteData(pkt.Payload);
            else if (pkt.Encoding == EncodingType.Adpcm) {
                byte[] pcm = AdpcmCodec.Decode(pkt.Payload, pkt.Channels, pkt.SampleRate);
                playback.WriteData(pcm);
            } else playback.WriteData(pkt.Payload);
        };
        server.OnLatencyReport += (latencyMs) => {
            Invoke(() => latencyChart.AddSample(latencyMs));
        };
        server.OnConfig += (enc, bitrate, bufferMs) => {
            capture.SetEncodingAndBitrate(enc, bitrate);
            Invoke(() => {
                playback.BufferDurationMs = bufferMs;
                if (playback.IsPlaying) playback.RestartWithNewBuffer();
                lblEncoding.Text = $"编码: {enc}";
                lblBitrate.Text = $"码率: {bitrate} kbps";
                lblBuffer.Text = $"缓冲: {bufferMs} ms";
                lblCaptureInfo.Text = $"{enc} {bitrate}kbps";
                Log($"配置已更新: {enc} {bitrate}kbps, 缓冲 {bufferMs}ms");
            });
        };
        capture.SetServer(server);
        this.Shown += OnFormShown;
    }

    private void BuildServerPage() {
        var lblTitle = new Label { Text = "服务器", Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(0, 0), AutoSize = true };
        // 状态卡片
        var statusCard = new RoundedPanel { Location = new Point(0, 44), Size = new Size(580, 56) };
        lblStatusDot = new Label { Text = "●", Font = new Font("Segoe UI", 14),
            ForeColor = Color.FromArgb(239, 68, 68), Location = new Point(16, 14), AutoSize = true };
        lblStatusText = new Label { Text = "未启动", Font = new Font("Microsoft YaHei UI", 11),
            ForeColor = Color.FromArgb(239, 68, 68), Location = new Point(40, 18), AutoSize = true };
        statusCard.Controls.AddRange([lblStatusDot, lblStatusText]);
        // 设备信息卡片
        var devCard = new RoundedPanel { Location = new Point(0, 112), Size = new Size(580, 72) };
        string hostname = "Unknown"; string localIp = "0.0.0.0";
        try { hostname = Dns.GetHostName();
            var ips = Dns.GetHostAddresses(Dns.GetHostName());
            localIp = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "0.0.0.0";
        } catch { }
        var lblHost = new Label { Text = hostname, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(16, 12), AutoSize = true };
        lblIpAddr = new Label { Text = localIp, Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(113, 128, 150), Location = new Point(16, 40), AutoSize = true };
        devCard.Controls.AddRange([lblHost, lblIpAddr]);
        // 音频捕获卡片
        var capCard = new RoundedPanel { Location = new Point(0, 196), Size = new Size(580, 56) };
        var lblCapTitle = new Label { Text = "音频捕获", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(113, 128, 150), Location = new Point(16, 8), AutoSize = true };
        lblCaptureInfo = new Label { Text = "Opus 64kbps", Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(16, 30), AutoSize = true };
        capCard.Controls.AddRange([lblCapTitle, lblCaptureInfo]);
        // 延迟曲线卡片
        var latCard = new RoundedPanel { Location = new Point(0, 264), Size = new Size(580, 108) };
        var lblLatTitle = new Label { Text = "网络延迟", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(113, 128, 150), Location = new Point(16, 4), AutoSize = true };
        latencyChart = new LatencyChartPanel { Location = new Point(12, 22), Size = new Size(556, 80) };
        latCard.Controls.AddRange([lblLatTitle, latencyChart]);
        // 日志区域
        txtLog = new TextBox { Location = new Point(0, 380), Size = new Size(580, 108),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(30, 30, 40), ForeColor = Color.FromArgb(100, 220, 150),
            Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None };
        pnlServer.Controls.AddRange([lblTitle, statusCard, devCard, capCard, latCard, txtLog]);
    }

    private void BuildPlayerPage() {
        var lblTitle = new Label { Text = "播放器", Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(0, 0), AutoSize = true };
        // 串流状态卡片
        var streamCard = new RoundedPanel { Location = new Point(0, 44), Size = new Size(580, 70) };
        lblStreamDot = new Label { Text = "●", Font = new Font("Segoe UI", 14),
            ForeColor = Color.FromArgb(245, 158, 11), Location = new Point(16, 22), AutoSize = true };
        lblStreamStatus = new Label { Text = "等待连接...", Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 158, 11), Location = new Point(44, 24), AutoSize = true };
        var lblStreamDesc = new Label { Text = "手机连接后自动开启 PC → 手机串流",
            Font = new Font("Microsoft YaHei UI", 9), ForeColor = Color.FromArgb(113, 128, 150),
            Location = new Point(44, 46), AutoSize = true };
        streamCard.Controls.AddRange([lblStreamDot, lblStreamStatus, lblStreamDesc]);
        // 配置信息卡片
        var cfgCard = new RoundedPanel { Location = new Point(0, 126), Size = new Size(580, 78) };
        var lblCfgTitle = new Label { Text = "当前配置", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(113, 128, 150), Location = new Point(16, 8), AutoSize = true };
        lblEncoding = new Label { Text = "编码: Opus", Font = new Font("Microsoft YaHei UI", 11),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(16, 32), AutoSize = true };
        lblBitrate = new Label { Text = "码率: 64 kbps", Font = new Font("Microsoft YaHei UI", 11),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(200, 32), AutoSize = true };
        lblBuffer = new Label { Text = "缓冲: 200 ms", Font = new Font("Microsoft YaHei UI", 11),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(380, 32), AutoSize = true };
        cfgCard.Controls.AddRange([lblCfgTitle, lblEncoding, lblBitrate, lblBuffer]);
        // 连接设备卡片
        var devCard = new RoundedPanel { Location = new Point(0, 216), Size = new Size(580, 56) };
        lblConnectedDevice = new Label { Text = "未连接任何设备", Font = new Font("Microsoft YaHei UI", 11),
            ForeColor = Color.FromArgb(113, 128, 150), Location = new Point(16, 18), AutoSize = true };
        devCard.Controls.Add(lblConnectedDevice);
        pnlPlayer.Controls.AddRange([lblTitle, streamCard, cfgCard, devCard]);
    }

    private void BuildSettingsPage() {
        var lblTitle = new Label { Text = "设置", Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(0, 0), AutoSize = true };
        // 服务设置卡片
        var srvCard = new RoundedPanel { Location = new Point(0, 44), Size = new Size(580, 60) };
        var lblPort = new Label { Text = "端口", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(113, 128, 150), Location = new Point(16, 8), AutoSize = true };
        txtPortSettings = new TextBox { Text = "9287", Font = new Font("Consolas", 11),
            Location = new Point(16, 30), Size = new Size(100, 26), BorderStyle = BorderStyle.FixedSingle };
        txtPortServer = txtPortSettings;
        srvCard.Controls.AddRange([lblPort, txtPortSettings]);
        // 音频设置卡片
        var audCard = new RoundedPanel { Location = new Point(0, 116), Size = new Size(580, 168) };
        var lblAudTitle = new Label { Text = "音频设置", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(113, 128, 150), Location = new Point(16, 8), AutoSize = true };
        var lblEnc = new Label { Text = "编码方式", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(16, 36), AutoSize = true };
        cboEncoding = new ComboBox { Location = new Point(120, 33), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        cboEncoding.Items.AddRange(["PCM", "Opus", "ADPCM"]); cboEncoding.SelectedIndex = 1;
        var lblBr = new Label { Text = "码率", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(16, 68), AutoSize = true };
        cboBitrate = new ComboBox { Location = new Point(120, 65), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        cboBitrate.Items.AddRange(["32 kbps", "64 kbps", "128 kbps", "192 kbps"]); cboBitrate.SelectedIndex = 1;
        var lblBuf = new Label { Text = "缓冲时间", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(16, 100), AutoSize = true };
        cboBuffer = new ComboBox { Location = new Point(120, 97), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        cboBuffer.Items.AddRange(["50 ms", "100 ms", "200 ms", "500 ms", "1000 ms"]); cboBuffer.SelectedIndex = 1;
        // 输出设备选择
        var lblDev = new Label { Text = "输出设备", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = Color.FromArgb(45, 55, 72), Location = new Point(16, 132), AutoSize = true };
        cboOutputDevice = new ComboBox { Location = new Point(120, 129), Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
        PopulateOutputDevices();
        cboOutputDevice.SelectedIndexChanged += OnOutputDeviceChanged;
        audCard.Controls.AddRange([lblAudTitle, lblEnc, cboEncoding, lblBr, cboBitrate, lblBuf, cboBuffer, lblDev, cboOutputDevice]);
        // 关于卡片
        var aboutCard = new RoundedPanel { Location = new Point(0, 296), Size = new Size(580, 56) };
        var lblAbout = new Label { Text = "AudioRelay v1.0 — 鸿蒙 ↔ Windows 音频串流",
            Font = new Font("Microsoft YaHei UI", 10), ForeColor = Color.FromArgb(113, 128, 150),
            Location = new Point(16, 18), AutoSize = true };
        aboutCard.Controls.Add(lblAbout);
        pnlSettings.Controls.AddRange([lblTitle, srvCard, audCard, aboutCard]);
    }

    private void SwitchPage(int index) {
        pnlServer.Visible = (index == 0);
        pnlPlayer.Visible = (index == 1);
        pnlSettings.Visible = (index == 2);
        btnNavServer.IsSelected = (index == 0);
        btnNavPlayer.IsSelected = (index == 1);
        btnNavSettings.IsSelected = (index == 2);
    }

    private async void OnFormShown(object? sender, EventArgs e) {
        await StartServer();
    }

    private async Task StartServer() {
        cts = new();
        try {
            int port = int.TryParse(txtPortSettings.Text, out int p) ? p : 9287;
            _ = server.StartAsync(port, cts.Token);
            await Task.Delay(300);
            btnStartStop.Text = "■ 停止服务"; btnStartStop.BackColor = Color.FromArgb(239, 68, 68);
            txtPortSettings.ReadOnly = true;
            lblStatusDot.ForeColor = Color.FromArgb(245, 158, 11);
            lblStatusText.Text = "等待连接..."; lblStatusText.ForeColor = Color.FromArgb(245, 158, 11);
            Log($"服务已启动，端口 {port}");
        } catch (Exception ex) { Log($"启动失败: {ex.Message}"); }
    }

    private async void OnStartClick(object? s, EventArgs e)
    {
        if (cts != null) {
            cts.Cancel(); cts = null;
            capture.Stop(); playback.Stop();
            btnStartStop.Text = "▶ 启动服务"; btnStartStop.BackColor = Color.FromArgb(37, 99, 235);
            txtPortSettings.ReadOnly = false;
            lblStatusDot.ForeColor = Color.FromArgb(239, 68, 68);
            lblStatusText.Text = "已停止"; lblStatusText.ForeColor = Color.FromArgb(239, 68, 68);
            capture.Stop();
            Log("服务已停止"); return;
        }
        await StartServer();
    }

    private void PopulateOutputDevices() {
        cboOutputDevice.Items.Clear();
        var names = AudioPlaybackService.GetDeviceNames();
        if (names.Length == 0) cboOutputDevice.Items.Add("无可用设备");
        else foreach (var n in names) cboOutputDevice.Items.Add(n);
        cboOutputDevice.SelectedIndex = 0;
    }

    private void OnOutputDeviceChanged(object? s, EventArgs e) {
        int idx = cboOutputDevice.SelectedIndex;
        if (idx >= 0) {
            playback.DeviceNumber = idx;
            if (playback.IsPlaying) {
                Invoke(() => { playback.RestartWithNewBuffer(); });
                Log($"输出设备已切换: {cboOutputDevice.SelectedItem}");
            }
        }
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
    public event Action<EncodingType, int, int>? OnConfig;
    public event Action<int>? OnLatencyReport;

    public async Task StartAsync(int port, CancellationToken tk) {
        listener = new(IPAddress.Any, port); listener.Start();
        OnLog?.Invoke($"监听端口 {port}");
        while (!tk.IsCancellationRequested) {
            try {
                client = await listener.AcceptTcpClientAsync(tk);
                client.NoDelay = true;
                stream = client.GetStream();
                OnConnected?.Invoke(true);

                // TCP 粘包/拆包处理：环形缓冲 + 按长度解析完整包
                const int HEADER_SIZE = 28;
                var recvBuf = new byte[262144]; // 256KB 累积缓冲
                int recvLen = 0; // 当前有效数据长度
                var readBuf = new byte[65536]; // 64KB 读取缓冲

                while (client.Connected && !tk.IsCancellationRequested) {
                    int n = await stream.ReadAsync(readBuf, 0, readBuf.Length, tk);
                    if (n == 0) break;

                    // 追加到累积缓冲区（如果不够大则扩容）
                    if (recvLen + n > recvBuf.Length)
                        Array.Resize(ref recvBuf, (recvLen + n) * 2);
                    Array.Copy(readBuf, 0, recvBuf, recvLen, n);
                    recvLen += n;

                    // 循环解析完整包
                    int offset = 0;
                    while (recvLen - offset >= HEADER_SIZE) {
                        int payloadLen = BitConverter.ToInt32(recvBuf, offset + 24);
                        int totalLen = HEADER_SIZE + payloadLen;
                        if (recvLen - offset < totalLen) break; // 数据不完整

                        var pktData = new byte[totalLen];
                        Array.Copy(recvBuf, offset, pktData, 0, totalLen);
                        var pkt = AudioPacket.Deserialize(pktData);

                        if (pkt.MsgType == MessageType.AudioData) OnAudioData?.Invoke(pkt);
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.Config && pkt.Payload.Length >= 9) {
                            var enc = (EncodingType)pkt.Payload[0];
                            int bitrate = BitConverter.ToInt32(pkt.Payload, 1);
                            int bufferMs = BitConverter.ToInt32(pkt.Payload, 5);
                            OnConfig?.Invoke(enc, bitrate, bufferMs);
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.LatencyReport && pkt.Payload.Length >= 4) {
                            int latencyMs = BitConverter.ToInt32(pkt.Payload, 0);
                            OnLatencyReport?.Invoke(latencyMs);
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.TimeSync && pkt.Payload.Length >= 8) {
                            // 时钟同步：回传手机时间戳 + PC时间戳 (16 bytes)
                            long pcTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var reply = new byte[16];
                            Array.Copy(pkt.Payload, 0, reply, 0, 8); // 回传手机时间戳
                            BitConverter.GetBytes(pcTime).CopyTo(reply, 8); // PC时间戳
                            var ack = new AudioPacket { MsgType = MessageType.Control, Command = ControlCommand.TimeSync,
                                Sequence = seq++, Timestamp = pcTime, SampleRate = 0, Channels = 0, BitsPerSample = 0, Payload = reply };
                            if (stream != null) await stream.WriteAsync(ack.Serialize());
                        }
                        offset += totalLen;
                    }

                    // 将未解析的数据移到缓冲头部
                    if (offset > 0) {
                        int remaining = recvLen - offset;
                        if (remaining > 0) Array.Copy(recvBuf, offset, recvBuf, 0, remaining);
                        recvLen = remaining;
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
    public string CurrentEncoding => encodingType.ToString();
    public int CurrentBitrate => opusBitrate;
    public void SetEncodingAndBitrate(EncodingType enc, int bitrate) {
        opusBitrate = bitrate;
        if (encodingType == enc) {
            // 编码没变，只更新码率
            if (enc == EncodingType.Opus && cap != null) {
                var newEnc = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                newEnc.Bitrate = opusBitrate * 1000;
                var old = opusEncoder;
                opusEncoder = newEnc;
                old?.Dispose();
            }
            return;
        }
        // 热切换编码：不停止捕获设备，只替换编码器
        lock (opusBuffer) { opusBuffer.Clear(); }
        if (enc == EncodingType.Opus) {
            var newEnc = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
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
        OnLog?.Invoke($"编码已切换: {enc} {opusBitrate}kbps");
    }

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
        var encoder = opusEncoder; // 本地变量快照，避免竞态 null
        if (encoder == null) return;
        lock (opusBuffer) {
            while (opusBuffer.Count >= frameTotal) {
                var frame = opusBuffer.GetRange(0, frameTotal).ToArray();
                opusBuffer.RemoveRange(0, frameTotal);
                byte[] outBuf = new byte[4000];
                int encoded = encoder.Encode(frame, 0, frameSamples, outBuf, 0, outBuf.Length);
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
    private int _deviceNumber = -1;
    public int BufferDurationMs { get; set; } = 200;
    public bool IsPlaying => wav != null;
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
        var fmt = new WaveFormat(48000, 16, 2);
        prov = new(fmt) { BufferDuration = TimeSpan.FromMilliseconds(BufferDurationMs), DiscardOnBufferOverflow = true };
        int devNum = _deviceNumber;
        if (devNum < 0 || devNum >= WaveOut.DeviceCount) devNum = 0;
        wav = new() { DeviceNumber = devNum };
        wav.Init(prov); wav.Play();
        string devName = devNum < WaveOut.DeviceCount ? WaveOut.GetCapabilities(devNum).ProductName : "Default";
        OnLog?.Invoke($"音频播放已启动 (设备: {devName}, 缓冲 {BufferDurationMs}ms)");
    }
    public void RestartWithNewBuffer() {
        bool wasPlaying = wav != null;
        Stop();
        if (wasPlaying) Start();
    }
    public void WriteData(byte[] d) => prov?.AddSamples(d, 0, d.Length);
    public void Stop() { wav?.Stop(); wav?.Dispose(); wav = null; prov = null; OnLog?.Invoke("音频播放已停止"); }
}

// 数据包协议
public enum MessageType : byte { Control = 0, AudioData = 1 }
public enum ControlCommand : byte { StartStream = 3, StopStream = 4, Volume = 5, Config = 6, LatencyReport = 7, TimeSync = 8 }
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
    public RoundedPanel() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Color.White; }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var brush = new SolidBrush(BackColor); g.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(226, 232, 240), 1); g.DrawPath(pen, path);
    }
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius) {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, radius, radius, 180, 90);
        path.AddArc(bounds.Right - radius, bounds.Y, radius, radius, 270, 90);
        path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - radius, radius, radius, 90, 90);
        path.CloseFigure(); return path;
    }
}

// 导航按钮
public class NavButton : Panel {
    public bool IsSelected { get; set; }
    private bool _hover;
    private string _text;
    public new event EventHandler? Click;
    public NavButton(string text) {
        _text = text; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.White;
    }
    protected override void OnClick(EventArgs e) { Click?.Invoke(this, e); base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(IsSelected ? Color.FromArgb(240, 245, 255) : _hover ? Color.FromArgb(248, 250, 252) : Color.White);
        if (IsSelected) {
            using var brush = new SolidBrush(Color.FromArgb(37, 99, 235));
            g.FillRectangle(brush, 0, 6, 3, Height - 12);
        }
        var textColor = IsSelected ? Color.FromArgb(37, 99, 235) : Color.FromArgb(100, 116, 139);
        var textFont = new Font("Microsoft YaHei UI", 10, IsSelected ? FontStyle.Bold : FontStyle.Regular);
        var textSize = g.MeasureString(_text, textFont);
        g.DrawString(_text, textFont, new SolidBrush(textColor), 20, (Height - textSize.Height) / 2);
    }
}

// 延迟曲线图表
public class LatencyChartPanel : Panel
{
    private readonly int[] _samples = new int[300]; // 300个采样点 (~30s @ 100ms刷新)
    private int _head;
    private int _count;
    private readonly object _lock = new();
    private System.Windows.Forms.Timer _timer;

    public LatencyChartPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(24, 28, 36);
        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (s, e) => Invalidate();
        _timer.Start();
    }

    public void AddSample(int latencyMs)
    {
        lock (_lock) {
            _samples[_head] = latencyMs;
            _head = (_head + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }
    }

    public void Clear()
    {
        lock (_lock) { _head = 0; _count = 0; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int w = Width, h = Height;
        if (w < 10 || h < 10) return;

        int[] snap;
        int snapCount;
        lock (_lock) {
            snap = new int[_count];
            int start = (_head - _count + _samples.Length) % _samples.Length;
            for (int i = 0; i < _count; i++)
                snap[i] = _samples[(start + i) % _samples.Length];
            snapCount = _count;
        }

        if (snapCount < 2) {
            using var nf = new Font("Microsoft YaHei UI", 9);
            using var nb = new SolidBrush(Color.FromArgb(80, 160, 160, 180));
            g.DrawString("等待数据...", nf, nb, w / 2 - 30, h / 2 - 8);
            return;
        }

        // 统计
        int minV = int.MaxValue, maxV = 0, sumV = 0;
        for (int i = 0; i < snapCount; i++) {
            if (snap[i] < minV) minV = snap[i];
            if (snap[i] > maxV) maxV = snap[i];
            sumV += snap[i];
        }
        int avgV = sumV / snapCount;

        // Y轴范围
        int yMax = Math.Max(maxV + 10, 50);
        int yMin = Math.Max(minV - 5, 0);
        if (yMax - yMin < 20) yMax = yMin + 20;

        float padL = 0, padR = 0, padT = 4, padB = 4;
        float plotW = w - padL - padR;
        float plotH = h - padT - padB;

        // 网格线
        using var gridPen = new Pen(Color.FromArgb(30, 50, 55, 65), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        int[] gridValues = [50, 100, 200, 500];
        using var gridFont = new Font("Consolas", 7.5f);
        using var gridBrush = new SolidBrush(Color.FromArgb(80, 140, 140, 160));
        foreach (int gv in gridValues) {
            if (gv >= yMin && gv <= yMax) {
                float gy = padT + plotH * (1f - (float)(gv - yMin) / (yMax - yMin));
                g.DrawLine(gridPen, padL, gy, padL + plotW, gy);
                g.DrawString($"{gv}ms", gridFont, gridBrush, 2, gy - 10);
            }
        }

        // 填充区域
        var pts = new PointF[snapCount];
        for (int i = 0; i < snapCount; i++) {
            float x = padL + (float)i / (snapCount - 1) * plotW;
            float y = padT + plotH * (1f - (float)(snap[i] - yMin) / (yMax - yMin));
            pts[i] = new PointF(x, y);
        }

        using var fillPath = new System.Drawing.Drawing2D.GraphicsPath();
        fillPath.AddLines(pts);
        fillPath.AddLine(pts[^1].X, padT + plotH, pts[0].X, padT + plotH);
        fillPath.CloseFigure();
        using var fillBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new RectangleF(padL, padT, plotW, plotH),
            Color.FromArgb(40, 37, 99, 235), Color.FromArgb(5, 37, 99, 235),
            System.Drawing.Drawing2D.LinearGradientMode.Vertical);
        g.FillPath(fillBrush, fillPath);

        // 曲线
        using var linePen = new Pen(Color.FromArgb(100, 180, 255), 1.8f);
        g.DrawLines(linePen, pts);

        // 统计文字
        using var statFont = new Font("Consolas", 8.5f, FontStyle.Bold);
        using var statBrush = new SolidBrush(Color.FromArgb(200, 180, 220, 255));
        string stats = $"avg {avgV}ms  |  min {minV}ms  |  max {maxV}ms  |  {snapCount} samples";
        var statSize = g.MeasureString(stats, statFont);
        g.DrawString(stats, statFont, statBrush, w - statSize.Width - 4, 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer?.Stop(); _timer?.Dispose(); }
        base.Dispose(disposing);
    }
}
