// 🤖 AI 辅助生成 — DeepSeek V4
// 项目: AudioRelayHM 鸿蒙↔Windows 音频串流

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Linq;
using System.Buffers;
using NAudio.Wave;
using Concentus.Structs;
using Concentus.Enums;

namespace AudioRelayWinUI;

static class Program
{
    [STAThread]
    static void Main() {
        using var mutex = new Mutex(false, "AudioRelayHM_SingleInstance");
        if (!mutex.WaitOne(0, false)) {
            // 已有实例运行 — 尝试恢复窗口
            var hwnd = FindWindow(null, "AudioRelay");
            if (hwnd != IntPtr.Zero) {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            return;
        }
        Application.Run(new MainForm());
    }

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const int SW_RESTORE = 9;
}

public class MainForm : Form
{
    // === 布局 ===
    private Panel navPanel = new();
    private Panel contentPanel = new();
    private Panel pnlServer = new();
    private Panel pnlPlayer = new();
    private Panel pnlSettings = new();
    private NavButton btnNavServer = new("服务器", "▣");
    private NavButton btnNavPlayer = new("播放器", "♫");
    private NavButton btnNavSettings = new("设置", "⚙");

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
    // 托盘图标
    private NotifyIcon? _notifyIcon;
    private bool _isExiting;

    public MainForm()
    {
        // === 整体框架 ===
        this.Text = "AudioRelay";
        this.Size = new Size(800, 560);
        this.MinimumSize = new Size(700, 480);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = UiTheme.Background;
        this.Font = new Font("Microsoft YaHei UI", 9.5f);

        // === 设置图标（强制使用 AppIcon.ico）===
        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                      ?? new Icon(Path.Combine(AppContext.BaseDirectory, "AppIcon.ico"));
        this.Icon = appIcon;
        // === 托盘图标 ===
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示", null, (s, e) => RestoreFromTray());
        trayMenu.Items.Add("-");
        trayMenu.Items.Add("退出", null, (s, e) => { _isExiting = true; Application.Exit(); });
        _notifyIcon = new NotifyIcon {
            Text = "AudioRelay",
            Icon = appIcon,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

        // === 左侧导航栏 ===
        navPanel.Dock = DockStyle.Left;
        navPanel.Width = 160;
        navPanel.BackColor = Color.White;
        var lblTitle = new Label { Text = "AudioRelay", Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold),
            ForeColor = UiTheme.Primary, Location = new Point(16, 20), AutoSize = true };
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
        btnStartStop.Click += OnStartClick;
        server.OnConnected += (ok) => Invoke(() => {
            if (ok) {
                lblStatusDot.ForeColor = UiTheme.Success;
                lblStatusText.Text = "已连接"; lblStatusText.ForeColor = UiTheme.Success;
                lblStreamDot.ForeColor = UiTheme.Success;
                lblStreamStatus.Text = "PC → 手机 串流中"; lblStreamStatus.ForeColor = UiTheme.Success;
                lblConnectedDevice.Text = "鸿蒙设备已连接";
                try { capture.Start(); }
                catch (Exception ex) { Log($"音频采集启动失败: {ex.Message}"); }
                // 手机→PC 音频不自动播放，需用户手动选择虚拟设备（如 VB-Cable）后开启
                Log($"鸿蒙端已连接，自动开启 PC→手机 ({capture.CurrentEncoding} {capture.CurrentBitrate}kbps)");
            } else {
                lblStatusDot.ForeColor = UiTheme.Warning;
                lblStatusText.Text = "等待连接..."; lblStatusText.ForeColor = UiTheme.Warning;
                lblStreamDot.ForeColor = UiTheme.Warning;
                lblStreamStatus.Text = "等待连接..."; lblStreamStatus.ForeColor = UiTheme.Warning;
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
        server.OnLatencyReport += (network, pcProcess, buffer, renderer) => {
            Invoke(() => latencyChart.AddSample(network + pcProcess + buffer + renderer, network, pcProcess, buffer, renderer));
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
        var lblTitle = new Label { Text = "服务器", Font = UiTheme.Font(18, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(0, 0), AutoSize = true };
        // 状态卡片
        var statusCard = new RoundedPanel { Location = new Point(0, 44), Size = new Size(580, 56) };
        lblStatusDot = new Label { Text = "●", Font = new Font("Segoe UI", 14),
            ForeColor = UiTheme.Danger, Location = new Point(16, 14), AutoSize = true };
        lblStatusText = new Label { Text = "未启动", Font = UiTheme.Font(11),
            ForeColor = UiTheme.Danger, Location = new Point(40, 18), AutoSize = true };
        btnStartStop = new FlatButton { Text = "▶ 启动服务",
            BackColor = UiTheme.Primary, ForeColor = Color.White,
            Location = new Point(440, 11), Size = new Size(124, 34) };
        statusCard.Controls.AddRange([lblStatusDot, lblStatusText, btnStartStop]);
        // 设备信息卡片
        var devCard = new RoundedPanel { Location = new Point(0, 112), Size = new Size(580, 72) };
        string hostname = "Unknown"; string localIp = "0.0.0.0";
        try { hostname = Dns.GetHostName();
            var ips = Dns.GetHostAddresses(Dns.GetHostName());
            localIp = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "0.0.0.0";
        } catch { }
        var lblHost = new Label { Text = hostname, Font = UiTheme.Font(12, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 12), AutoSize = true };
        lblIpAddr = new Label { Text = localIp, Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 40), AutoSize = true };
        devCard.Controls.AddRange([lblHost, lblIpAddr]);
        // 音频捕获卡片
        var capCard = new RoundedPanel { Location = new Point(0, 196), Size = new Size(580, 56) };
        var lblCapTitle = new Label { Text = "音频捕获", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        lblCaptureInfo = new Label { Text = "Opus 64kbps", Font = UiTheme.Font(11, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 30), AutoSize = true };
        capCard.Controls.AddRange([lblCapTitle, lblCaptureInfo]);
        // 延迟曲线卡片
        var latCard = new RoundedPanel { Location = new Point(0, 264), Size = new Size(580, 130) };
        var lblLatTitle = new Label { Text = "端到端延迟", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 4), AutoSize = true };
        latencyChart = new LatencyChartPanel { Location = new Point(12, 22), Size = new Size(556, 102) };
        latCard.Controls.AddRange([lblLatTitle, latencyChart]);
        // 日志区域
        txtLog = new TextBox { Location = new Point(0, 402), Size = new Size(580, 108),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = UiTheme.ChartBg, ForeColor = Color.FromArgb(100, 220, 150),
            Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None };
        pnlServer.Controls.AddRange([lblTitle, statusCard, devCard, capCard, latCard, txtLog]);
    }

    private void BuildPlayerPage() {
        var lblTitle = new Label { Text = "播放器", Font = UiTheme.Font(18, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(0, 0), AutoSize = true };
        // 串流状态卡片
        var streamCard = new RoundedPanel { Location = new Point(0, 44), Size = new Size(580, 70) };
        lblStreamDot = new Label { Text = "●", Font = new Font("Segoe UI", 14),
            ForeColor = UiTheme.Warning, Location = new Point(16, 22), AutoSize = true };
        lblStreamStatus = new Label { Text = "等待连接...", Font = UiTheme.Font(12, FontStyle.Bold),
            ForeColor = UiTheme.Warning, Location = new Point(44, 24), AutoSize = true };
        var lblStreamDesc = new Label { Text = "手机连接后自动开启 PC → 手机串流",
            Font = UiTheme.Font(9), ForeColor = UiTheme.TextSecondary,
            Location = new Point(44, 46), AutoSize = true };
        streamCard.Controls.AddRange([lblStreamDot, lblStreamStatus, lblStreamDesc]);
        // 配置信息卡片
        var cfgCard = new RoundedPanel { Location = new Point(0, 126), Size = new Size(580, 78) };
        var lblCfgTitle = new Label { Text = "当前配置", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        lblEncoding = new Label { Text = "编码: PCM", Font = UiTheme.Font(11),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 32), AutoSize = true };
        lblBitrate = new Label { Text = "码率: 0 kbps", Font = UiTheme.Font(11),
            ForeColor = UiTheme.TextPrimary, Location = new Point(200, 32), AutoSize = true };
        lblBuffer = new Label { Text = "缓冲: 0 ms", Font = UiTheme.Font(11),
            ForeColor = UiTheme.TextPrimary, Location = new Point(380, 32), AutoSize = true };
        cfgCard.Controls.AddRange([lblCfgTitle, lblEncoding, lblBitrate, lblBuffer]);
        // 连接设备卡片
        var devCard = new RoundedPanel { Location = new Point(0, 216), Size = new Size(580, 56) };
        lblConnectedDevice = new Label { Text = "未连接任何设备", Font = UiTheme.Font(11),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 18), AutoSize = true };
        devCard.Controls.Add(lblConnectedDevice);
        pnlPlayer.Controls.AddRange([lblTitle, streamCard, cfgCard, devCard]);
    }

    private void BuildSettingsPage() {
        var lblTitle = new Label { Text = "设置", Font = UiTheme.Font(18, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(0, 0), AutoSize = true };
        // 服务设置卡片
        var srvCard = new RoundedPanel { Location = new Point(0, 44), Size = new Size(580, 60) };
        var lblPort = new Label { Text = "端口", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        txtPortSettings = new TextBox { Text = "9287", Font = new Font("Consolas", 11),
            Location = new Point(16, 30), Size = new Size(100, 26), BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.InputBg };
        txtPortServer = txtPortSettings;
        srvCard.Controls.AddRange([lblPort, txtPortSettings]);
        // 音频设置卡片
        var audCard = new RoundedPanel { Location = new Point(0, 116), Size = new Size(580, 168) };
        var lblAudTitle = new Label { Text = "音频设置", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        var lblEnc = new Label { Text = "编码方式", Font = new Font("Microsoft YaHei UI", 10),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 36), AutoSize = true };
        cboEncoding = new ComboBox { Location = new Point(120, 33), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        cboEncoding.Items.AddRange(["PCM", "Opus", "ADPCM"]); cboEncoding.SelectedIndex = 0;
        var lblBr = new Label { Text = "码率", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 68), AutoSize = true };
        cboBitrate = new ComboBox { Location = new Point(120, 65), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        cboBitrate.Items.AddRange(["32 kbps", "64 kbps", "128 kbps", "192 kbps"]); cboBitrate.SelectedIndex = 1;
        var lblBuf = new Label { Text = "缓冲时间", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 100), AutoSize = true };
        cboBuffer = new ComboBox { Location = new Point(120, 97), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        cboBuffer.Items.AddRange(["0 ms", "50 ms", "100 ms", "200 ms", "500 ms", "1000 ms"]); cboBuffer.SelectedIndex = 0;
        // 本地默认配置：变更立即生效（手机端 CONFIG 下发时会被覆盖，符合"手机端主控"设计）
        cboEncoding.SelectedIndexChanged += OnLocalConfigChanged;
        cboBitrate.SelectedIndexChanged += OnLocalConfigChanged;
        cboBuffer.SelectedIndexChanged += OnLocalConfigChanged;
        // 输出设备选择
        var lblDev = new Label { Text = "输出设备", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 132), AutoSize = true };
        cboOutputDevice = new ComboBox { Location = new Point(120, 129), Width = 280, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        PopulateOutputDevices();
        cboOutputDevice.SelectedIndexChanged += OnOutputDeviceChanged;
        audCard.Controls.AddRange([lblAudTitle, lblEnc, cboEncoding, lblBr, cboBitrate, lblBuf, cboBuffer, lblDev, cboOutputDevice]);
        // 关于卡片
        var aboutCard = new RoundedPanel { Location = new Point(0, 296), Size = new Size(580, 56) };
        var lblAbout = new Label { Text = "AudioRelay v1.0 — 鸿蒙 ↔ Windows 音频串流",
            Font = UiTheme.Font(10), ForeColor = UiTheme.TextSecondary,
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
            if (!server.Listening) {
                // StartAsync 已通过 OnLog 输出具体原因（如端口被占用）
                cts.Cancel(); cts = null;
                lblStatusDot.ForeColor = UiTheme.Danger;
                lblStatusText.Text = "启动失败"; lblStatusText.ForeColor = UiTheme.Danger;
                Log($"服务启动失败：端口 {port} 不可用");
                return;
            }
            btnStartStop.Text = "■ 停止服务"; btnStartStop.BackColor = UiTheme.Danger;
            txtPortSettings.ReadOnly = true;
            lblStatusDot.ForeColor = UiTheme.Warning;
            lblStatusText.Text = "等待连接..."; lblStatusText.ForeColor = UiTheme.Warning;
            Log($"服务已启动，端口 {port}");
        } catch (Exception ex) { Log($"启动失败: {ex.Message}"); }
    }

    private async void OnStartClick(object? s, EventArgs e)
    {
        if (cts != null) {
            cts.Cancel(); cts = null;
            server.Stop(); // 释放 TCP 监听端口，否则再次启动会报端口占用
            capture.Stop(); playback.Stop();
            btnStartStop.Text = "▶ 启动服务"; btnStartStop.BackColor = UiTheme.Primary;
            txtPortSettings.ReadOnly = false;
            lblStatusDot.ForeColor = UiTheme.Danger;
            lblStatusText.Text = "已停止"; lblStatusText.ForeColor = UiTheme.Danger;
            capture.Stop();
            Log("服务已停止"); return;
        }
        await StartServer();
    }

    private void PopulateOutputDevices() {
        try {
            cboOutputDevice.Items.Clear();
            var names = AudioPlaybackService.GetDeviceNames();
            if (names.Length == 0) cboOutputDevice.Items.Add("无可用设备");
            else foreach (var n in names) cboOutputDevice.Items.Add(n);
            cboOutputDevice.SelectedIndex = 0;
        } catch {
            // 无音频设备/驱动异常时窗体仍可正常启动（构造函数调用路径不能抛异常）
            cboOutputDevice.Items.Clear();
            cboOutputDevice.Items.Add("无可用设备");
            cboOutputDevice.SelectedIndex = 0;
        }
    }

    // 设置页本地默认配置变更（编码/码率/缓冲）。手机端 CONFIG 命令会覆盖这些本地值。
    private void OnLocalConfigChanged(object? s, EventArgs e) {
        var enc = (EncodingType)Math.Max(cboEncoding.SelectedIndex, 0);
        int bitrate = 64;
        int[] brMap = [32, 64, 128, 192];
        if (cboBitrate.SelectedIndex >= 0 && cboBitrate.SelectedIndex < brMap.Length)
            bitrate = brMap[cboBitrate.SelectedIndex];
        capture.SetEncodingAndBitrate(enc, bitrate);

        int bufferMs = 0;
        int[] bufMap = [0, 50, 100, 200, 500, 1000];
        if (cboBuffer.SelectedIndex >= 0 && cboBuffer.SelectedIndex < bufMap.Length)
            bufferMs = bufMap[cboBuffer.SelectedIndex];
        playback.BufferDurationMs = bufferMs;
        if (playback.IsPlaying) playback.RestartWithNewBuffer();

        lblCaptureInfo.Text = $"{enc} {bitrate}kbps";
        Log($"本地配置已应用: {enc} {bitrate}kbps, 缓冲 {bufferMs}ms");
    }

    private void OnOutputDeviceChanged(object? s, EventArgs e) {
        int idx = cboOutputDevice.SelectedIndex;
        if (idx >= 0) {
            playback.DeviceNumber = idx;
            if (idx == 0) {
                // 默认设备（扬声器）不播放手机音频
                if (playback.IsPlaying) {
                    playback.Stop();
                    Log($"手机→PC 已停止（避免输出到扬声器）");
                }
            } else {
                // 虚拟设备（如 VB-Cable）自动开始播放
                if (!playback.IsPlaying && server.Connected) {
                    playback.Start();
                    Log($"手机→PC 已开始，输出到: {cboOutputDevice.SelectedItem}");
                } else if (playback.IsPlaying) {
                    Invoke(() => { playback.RestartWithNewBuffer(); });
                    Log($"输出设备已切换: {cboOutputDevice.SelectedItem}");
                }
            }
        }
    }

    private void Log(string msg) {
        // BeginInvoke：不阻塞网络/音频线程；窗体销毁后静默丢弃
        if (!IsHandleCreated || Disposing) return;
        BeginInvoke(() => {
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
            // 日志上限：超过 800 行裁掉前半，防止长时间运行内存无限增长
            if (txtLog.Lines.Length > 800) {
                var lines = txtLog.Lines;
                txtLog.Lines = lines.Skip(lines.Length - 400).ToArray();
            }
            txtLog.SelectionStart = txtLog.Text.Length; txtLog.ScrollToCaret();
        });
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        // 仅拦截用户手动关闭（最小化到托盘）；系统关机/注销时放行，避免阻止关机
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing) {
            e.Cancel = true;
            this.Hide();
            return;
        }
        _notifyIcon?.Dispose();
        cts?.Cancel(); capture.Stop(); playback.Stop();
        server.Stop(); // 释放 TCP 监听端口，避免进程退出前端口一直被占用
        base.OnFormClosing(e);
    }

    private void RestoreFromTray() {
        this.Show();
        this.WindowState = FormWindowState.Normal;
        this.Activate();
    }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        if (this.WindowState == FormWindowState.Minimized) {
            this.Hide();
        }
    }
}

// 网络服务
