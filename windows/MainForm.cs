// 🤖 AI 辅助生成 — DeepSeek V4
// 项目: AudioRelayHM 鸿蒙↔Windows 音频串流

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Linq;
using System.Buffers;
using System.IO;
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
    private Panel topBar = new();
    private Panel contentPanel = new();
    private Panel pnlServer = new();
    private Panel pnlPlayer = new();
    private Panel pnlSettings = new();
    private FlatButton btnNavServer = new();
    private FlatButton btnNavPlayer = new();
    private FlatButton btnNavSettings = new();

    // === 服务器页控件 ===
    private Label lblStatusDot = new();
    private Label lblStatusText = new();
    private Label lblIpAddr = new();
    private Label lblCaptureInfo = new();
    private TextBox txtLog = new();
    private TextBox txtPortServer = new();
    private Button btnStartStop = new();
    private LatencyChartPanel latencyChart = new();
    private RoundedPanel logCard = new();

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
        this.MinimumSize = new Size(800, 500);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = UiTheme.Background;
        this.Font = new Font("Microsoft YaHei UI", 9.5f);
        // 无边框 + 自绘标题栏（去掉系统标题栏的图标/文字）
        this.FormBorderStyle = FormBorderStyle.None;

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

        // === 顶部导航栏（logo + 导航 + 窗口控制按钮，单条）===
        topBar.Dock = DockStyle.Top;
        topBar.Height = 52;
        topBar.BackColor = UiTheme.Background; // 与窗体背景同色，融为一体
        var logoBox = new PictureBox { Image = appIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(20, 9), Size = new Size(34, 34) };
        var lblTitle = new Label { Text = "AudioRelay", Font = UiTheme.Font(15, FontStyle.Bold),
            ForeColor = UiTheme.Primary, Location = new Point(60, 14), AutoSize = true };
        topBar.Controls.AddRange([logoBox, lblTitle]);
        btnNavServer.Text = "服务器"; btnNavPlayer.Text = "播放器"; btnNavSettings.Text = "设置";
        foreach (var b in new[] { btnNavServer, btnNavPlayer, btnNavSettings }) {
            b.Size = new Size(80, 34);
            b.BorderColor = Color.Transparent;
            b.Font = UiTheme.Font(10, FontStyle.Bold);
        }
        btnNavServer.Location = new Point(0, 9);
        btnNavPlayer.Location = new Point(0, 9);
        btnNavSettings.Location = new Point(0, 9);
        btnNavServer.Click += (s, e) => SwitchPage(0);
        btnNavPlayer.Click += (s, e) => SwitchPage(1);
        btnNavSettings.Click += (s, e) => SwitchPage(2);
        // 窗口控制按钮：Dock=Right 贴右缘，随窗口宽度自适应（不被遮挡）
        var btnClose = new WinButton { Type = WinButton.BtnType.Close, Dock = DockStyle.Right, Width = 40 };
        var btnMax = new WinButton { Type = WinButton.BtnType.Maximize, Dock = DockStyle.Right, Width = 40 };
        var btnMin = new WinButton { Type = WinButton.BtnType.Minimize, Dock = DockStyle.Right, Width = 40 };
        btnMin.Click += (s, e) => this.WindowState = FormWindowState.Minimized;
        btnMax.Click += (s, e) => this.WindowState = this.WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;
        btnClose.Click += (s, e) => { _isExiting = true; Application.Exit(); };
        topBar.Controls.AddRange([btnNavServer, btnNavPlayer, btnNavSettings, btnClose, btnMax, btnMin]);
        // 顶栏空白区拖拽移动窗口；双击最大化/还原
        topBar.MouseDown += (s, e) => {
            ReleaseCapture();
            SendMessage(this.Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        };
        topBar.MouseDoubleClick += (s, e) =>
            this.WindowState = this.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
        var topSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = UiTheme.Border };
        topBar.Controls.Add(topSep);

        // === 内容区（绝对定位在顶栏下方，避免 Dock=Fill 被顶栏覆盖）===
        contentPanel.Location = new Point(0, 52);
        contentPanel.Size = new Size(ClientSize.Width, ClientSize.Height - 52);
        contentPanel.Padding = new Padding(20, 16, 20, 16);
        pnlServer.Dock = DockStyle.Fill; pnlPlayer.Dock = DockStyle.Fill; pnlSettings.Dock = DockStyle.Fill;
        BuildServerPage(); BuildPlayerPage(); BuildSettingsPage();
        contentPanel.Controls.AddRange([pnlSettings, pnlPlayer, pnlServer]);
        Controls.Add(topBar);
        Controls.Add(contentPanel);
        // 尺寸自适应：导航按钮居中 + 日志区高度跟随窗口高度
        this.Resize += (s, e) => {
            contentPanel.Size = new Size(ClientSize.Width, ClientSize.Height - 52);
            LayoutNavButtons();
            LayoutLogArea();
            ApplyRoundedRegion();
        };
        LayoutNavButtons();
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
        // 状态横幅：状态文字 + 主操作按钮（全宽，主操作醒目蓝）
        var banner = new RoundedPanel { Location = new Point(0, 24), Size = new Size(760, 64) };
        lblStatusDot = new Label { Text = "●", Font = new Font("Segoe UI", 14),
            ForeColor = UiTheme.Danger, Location = new Point(20, 20), AutoSize = true };
        lblStatusText = new Label { Text = "未启动", Font = UiTheme.Font(14, FontStyle.Bold),
            ForeColor = UiTheme.Danger, Location = new Point(44, 22), AutoSize = true };
        btnStartStop = new FlatButton { Text = "▶ 启动服务",
            BackColor = UiTheme.Primary, ForeColor = Color.White,
            Location = new Point(616, 15), Size = new Size(124, 34) };
        banner.Controls.AddRange([lblStatusDot, lblStatusText, btnStartStop]);
        // 双栏：设备信息 | 音频捕获
        var devCard = new RoundedPanel { Location = new Point(0, 92), Size = new Size(370, 76) };
        string hostname = "Unknown"; string localIp = "0.0.0.0";
        try { hostname = Dns.GetHostName();
            var ips = Dns.GetHostAddresses(Dns.GetHostName());
            localIp = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "0.0.0.0";
        } catch { }
        var lblHost = new Label { Text = hostname, Font = UiTheme.Font(12, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 14), AutoSize = true };
        lblIpAddr = new Label { Text = localIp, Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 42), AutoSize = true };
        devCard.Controls.AddRange([lblHost, lblIpAddr]);
        var capCard = new RoundedPanel { Location = new Point(390, 92), Size = new Size(370, 76) };
        var lblCapTitle = new Label { Text = "音频捕获", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 10), AutoSize = true };
        lblCaptureInfo = new Label { Text = "Opus 64kbps · 48kHz · 2ch", Font = UiTheme.Font(11, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 32), AutoSize = true };
        capCard.Controls.AddRange([lblCapTitle, lblCaptureInfo]);
        // 延迟大图（音频工具核心视觉）
        var latCard = new RoundedPanel { Location = new Point(0, 176), Size = new Size(760, 184) };
        var lblLatTitle = new Label { Text = "端到端延迟", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 4), AutoSize = true };
        latencyChart = new LatencyChartPanel { Location = new Point(12, 22), Size = new Size(736, 156) };
        latCard.Controls.AddRange([lblLatTitle, latencyChart]);
        // 日志区（浅色，与整体风格一致，不突兀）
        logCard = new RoundedPanel { Location = new Point(0, 368), Size = new Size(760, 108) };
        var lblLogTitle = new Label { Text = "实时日志", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        txtLog = new TextBox { Location = new Point(10, 30), Size = new Size(740, 72),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White, ForeColor = Color.FromArgb(51, 65, 85),
            Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None };
        logCard.Controls.AddRange([lblLogTitle, txtLog]);
        pnlServer.Controls.AddRange([banner, devCard, capCard, latCard, logCard]);
    }

    private void BuildPlayerPage() {
        // 串流状态横幅
        var streamCard = new RoundedPanel { Location = new Point(0, 0), Size = new Size(760, 64) };
        lblStreamDot = new Label { Text = "●", Font = new Font("Segoe UI", 14),
            ForeColor = UiTheme.Warning, Location = new Point(20, 22), AutoSize = true };
        lblStreamStatus = new Label { Text = "等待连接...", Font = UiTheme.Font(13, FontStyle.Bold),
            ForeColor = UiTheme.Warning, Location = new Point(44, 22), AutoSize = true };
        var lblStreamDesc = new Label { Text = "手机连接后自动开启 PC → 手机串流",
            Font = UiTheme.Font(9), ForeColor = UiTheme.TextSecondary,
            Location = new Point(430, 26), AutoSize = true };
        streamCard.Controls.AddRange([lblStreamDot, lblStreamStatus, lblStreamDesc]);
        // 配置信息（全宽横排三项）
        var cfgCard = new RoundedPanel { Location = new Point(0, 72), Size = new Size(760, 76) };
        var lblCfgTitle = new Label { Text = "当前配置", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        lblEncoding = new Label { Text = "编码: PCM", Font = UiTheme.Font(11, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 36), AutoSize = true };
        lblBitrate = new Label { Text = "码率: 0 kbps", Font = UiTheme.Font(11, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(300, 36), AutoSize = true };
        lblBuffer = new Label { Text = "缓冲: 0 ms", Font = UiTheme.Font(11, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary, Location = new Point(560, 36), AutoSize = true };
        cfgCard.Controls.AddRange([lblCfgTitle, lblEncoding, lblBitrate, lblBuffer]);
        // 连接设备
        var devCard = new RoundedPanel { Location = new Point(0, 156), Size = new Size(760, 64) };
        lblConnectedDevice = new Label { Text = "未连接任何设备", Font = UiTheme.Font(11),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 22), AutoSize = true };
        devCard.Controls.Add(lblConnectedDevice);
        pnlPlayer.Controls.AddRange([streamCard, cfgCard, devCard]);
    }

    private void BuildSettingsPage() {
        // 双栏：服务设置 | 音频设置
        var srvCard = new RoundedPanel { Location = new Point(0, 0), Size = new Size(370, 140) };
        var lblPort = new Label { Text = "端口", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        txtPortSettings = new TextBox { Text = "9287", Font = new Font("Consolas", 11),
            Location = new Point(16, 32), Size = new Size(100, 26), BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.InputBg };
        txtPortServer = txtPortSettings;
        srvCard.Controls.AddRange([lblPort, txtPortSettings]);
        var audCard = new RoundedPanel { Location = new Point(390, 0), Size = new Size(370, 140) };
        var lblAudTitle = new Label { Text = "音频设置", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        var lblEnc = new Label { Text = "编码方式", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextPrimary, Location = new Point(16, 36), AutoSize = true };
        cboEncoding = new ComboBox { Location = new Point(120, 33), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        cboEncoding.Items.AddRange(["PCM", "Opus", "ADPCM"]); cboEncoding.SelectedIndex = 0;
        var lblBr = new Label { Text = "码率", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 76), AutoSize = true };
        cboBitrate = new ComboBox { Location = new Point(120, 73), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        cboBitrate.Items.AddRange(["32 kbps", "64 kbps", "128 kbps", "192 kbps"]); cboBitrate.SelectedIndex = 1;
        var lblBuf = new Label { Text = "缓冲时间", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 116), AutoSize = true };
        cboBuffer = new ComboBox { Location = new Point(120, 113), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        cboBuffer.Items.AddRange(["0 ms", "50 ms", "100 ms", "200 ms", "500 ms", "1000 ms"]); cboBuffer.SelectedIndex = 0;
        // 本地默认配置：变更立即生效（手机端 CONFIG 下发时会被覆盖，符合"手机端主控"设计）
        cboEncoding.SelectedIndexChanged += OnLocalConfigChanged;
        cboBitrate.SelectedIndexChanged += OnLocalConfigChanged;
        cboBuffer.SelectedIndexChanged += OnLocalConfigChanged;
        audCard.Controls.AddRange([lblAudTitle, lblEnc, cboEncoding, lblBr, cboBitrate, lblBuf, cboBuffer]);
        // 输出设备全宽
        var outCard = new RoundedPanel { Location = new Point(0, 148), Size = new Size(760, 64) };
        var lblDev = new Label { Text = "输出设备", Font = UiTheme.Font(10),
            ForeColor = UiTheme.TextSecondary, Location = new Point(16, 8), AutoSize = true };
        cboOutputDevice = new ComboBox { Location = new Point(16, 32), Width = 560, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = UiTheme.InputBg };
        PopulateOutputDevices();
        cboOutputDevice.SelectedIndexChanged += OnOutputDeviceChanged;
        outCard.Controls.AddRange([lblDev, cboOutputDevice]);
        // 关于
        var aboutCard = new RoundedPanel { Location = new Point(0, 220), Size = new Size(760, 50) };
        var lblAbout = new Label { Text = "AudioRelay v1.0 — 鸿蒙 ↔ Windows 音频串流",
            Font = UiTheme.Font(10), ForeColor = UiTheme.TextSecondary,
            Location = new Point(16, 16), AutoSize = true };
        aboutCard.Controls.Add(lblAbout);
        pnlSettings.Controls.AddRange([srvCard, audCard, outCard, aboutCard]);
    }

    // 顶部导航按钮组：随窗口宽度水平居中（避免与右侧窗口按钮重叠）
    private void LayoutNavButtons() {
        int totalW = 80 * 3; // 三个 80px 导航按钮
        int x = Math.Max(0, (topBar.Width - totalW) / 2);
        btnNavServer.Location = new Point(x, 9);
        btnNavPlayer.Location = new Point(x + 80, 9);
        btnNavSettings.Location = new Point(x + 160, 9);
    }

    // 日志区高度跟随窗口高度（内容区变矮时压缩日志，避免被裁）
    private void LayoutLogArea() {
        int h = pnlServer.Height - 368;
        if (h < 60) h = 60;
        logCard.Height = h;
        txtLog.Height = h - 38;
        if (txtLog.Height < 40) txtLog.Height = 40;
    }

    // 自绘圆角 Region（替代 WS_THICKFRAME：避免顶部 8px 白色非客户区边框）
    private void ApplyRoundedRegion() {
        if (WindowState == FormWindowState.Maximized) { Region = null; return; }
        int r = 12;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2 - 1, Height - r * 2 - 1, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2 - 1, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    private void SwitchPage(int index) {
        pnlServer.Visible = (index == 0);
        pnlPlayer.Visible = (index == 1);
        pnlSettings.Visible = (index == 2);
        ApplyNavStyle(btnNavServer, index == 0);
        ApplyNavStyle(btnNavPlayer, index == 1);
        ApplyNavStyle(btnNavSettings, index == 2);
    }

    // 顶部导航按钮样式：选中=浅蓝底+主蓝字，未选=白底+灰字
    private static void ApplyNavStyle(FlatButton b, bool selected) {
        b.BackColor = selected ? UiTheme.PrimaryLight : Color.White;
        b.ForeColor = selected ? UiTheme.Primary : UiTheme.TextSecondary;
    }

    private async void OnFormShown(object? sender, EventArgs e) {
        ApplyRoundedRegion();
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

    // 自动落盘日志目录（exe 同级 logs/），当日文件，5MB 轮转到 .1
    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "logs");
    private readonly object _logFileLock = new();

    private void Log(string msg) {
        // 文件落盘：问题不复现时可按日期翻查
        try {
            lock (_logFileLock) {
                Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, $"AudioRelay_{DateTime.Now:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
        } catch { /* 日志写失败不影响主流程 */ }

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

    // 无边框窗体：自绘圆角 Region（见 ApplyRoundedRegion），不再使用 WS_THICKFRAME（会引入顶部白条）
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // 顶栏拖拽：模拟标题栏拖动
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
}

// 网络服务
