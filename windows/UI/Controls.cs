// UI 主题与自绘控件
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AudioRelayWinUI;

// ========== UI 主题（统一色板与字体） ==========
public static class UiTheme {
    public static readonly Color Primary = Color.FromArgb(37, 99, 235);      // 主蓝
    public static readonly Color PrimaryDark = Color.FromArgb(29, 78, 216);  // 按下
    public static readonly Color PrimaryLight = Color.FromArgb(219, 234, 254); // 选中背景
    public static readonly Color Success = Color.FromArgb(16, 185, 129);
    public static readonly Color Warning = Color.FromArgb(245, 158, 11);
    public static readonly Color Danger = Color.FromArgb(239, 68, 68);
    public static readonly Color Background = Color.FromArgb(241, 245, 249);  // 窗口背景（浅灰蓝）
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(216, 224, 234); // 卡片/分隔线边框（微加深增强模块感）
    public static readonly Color TextPrimary = Color.FromArgb(15, 23, 42);
    public static readonly Color TextSecondary = Color.FromArgb(100, 116, 139);
    public static readonly Color InputBg = Color.FromArgb(248, 250, 252);
    public static readonly Color Hover = Color.FromArgb(248, 250, 252);
    public static readonly Color ChartBg = Color.FromArgb(24, 28, 36);
    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new("Microsoft YaHei UI", size, style);
    public static Color Adjust(Color c, float factor) => Color.FromArgb(c.A,
        Math.Min(255, (int)(c.R * factor)), Math.Min(255, (int)(c.G * factor)), Math.Min(255, (int)(c.B * factor)));
}

// 圆角悬浮按钮（hover 提亮 / 按下加深）
public class FlatButton : Button {
    public int CornerRadius { get; set; } = 8;
    public Color BorderColor { get; set; } = Color.Transparent; // 非透明时绘制 1px 圆角描边（线性按钮）
    private bool _hover;
    private bool _pressed;
    public FlatButton() {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = UiTheme.Font(10, FontStyle.Bold);
        BackColor = UiTheme.Primary;
        ForeColor = Color.White;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        Color fill = BackColor;
        if (!Enabled) fill = Color.FromArgb(148, 163, 184);
        else if (_pressed) fill = UiTheme.Adjust(fill, 0.85f);
        else if (_hover) fill = UiTheme.Adjust(fill, 1.08f);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (BorderColor.A != 0) {
            using var pen = new Pen(BorderColor, 1);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
    internal static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius) {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int r = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
        path.AddArc(bounds.X, bounds.Y, r, r, 180, 90);
        path.AddArc(bounds.Right - r, bounds.Y, r, r, 270, 90);
        path.AddArc(bounds.Right - r, bounds.Bottom - r, r, r, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// 圆角面板
public class RoundedPanel : Panel {
    public int CornerRadius { get; set; } = 12;
    public RoundedPanel() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true); BackColor = UiTheme.Card; }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = FlatButton.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var brush = new SolidBrush(BackColor); g.FillPath(brush, path);
        using var pen = new Pen(UiTheme.Border, 1); g.DrawPath(pen, path);
    }
}

// 导航按钮（图标 + 圆角选中态）
public class NavButton : Panel {
    public bool IsSelected { get; set; }
    private bool _hover;
    private readonly string _icon;
    private readonly string _text;
    public new event EventHandler? Click;
    public NavButton(string text, string icon = "") {
        _text = text; _icon = icon; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.White;
    }
    protected override void OnClick(EventArgs e) { Click?.Invoke(this, e); base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(IsSelected ? UiTheme.PrimaryLight : _hover ? UiTheme.Hover : Color.White);
        // 圆角选中指示条
        if (IsSelected) {
            using var brush = new SolidBrush(UiTheme.Primary);
            using var bar = new System.Drawing.Drawing2D.GraphicsPath();
            int bw = 4, bh = Height - 20, by = 10;
            bar.AddArc(0, by, bw, bw, 180, 90);
            bar.AddArc(0, by + bh - bw, bw, bw, 90, 90);
            bar.CloseFigure();
            g.FillPath(brush, bar);
        }
        var textColor = IsSelected ? UiTheme.Primary : UiTheme.TextSecondary;
        // 图标（普通 Unicode 符号，GDI+ 下稳定渲染）
        if (!string.IsNullOrEmpty(_icon)) {
            using var iconFont = new Font("Segoe UI Symbol", 11);
            var iconSize = g.MeasureString(_icon, iconFont);
            using var iconBrush = new SolidBrush(IsSelected ? UiTheme.Primary : Color.FromArgb(148, 163, 184));
            g.DrawString(_icon, iconFont, iconBrush, 22, (Height - iconSize.Height) / 2);
        }
        var textFont = UiTheme.Font(10, IsSelected ? FontStyle.Bold : FontStyle.Regular);
        var textSize = g.MeasureString(_text, textFont);
        float tx = string.IsNullOrEmpty(_icon) ? 20 : 48;
        using var textBrush = new SolidBrush(textColor);
        g.DrawString(_text, textFont, textBrush, tx, (Height - textSize.Height) / 2);
    }
}

// 延迟曲线图表（堆叠显示）
public class LatencyChartPanel : Panel
{
    private const int MAX_SAMPLES = 300;
    private readonly int[] _total = new int[MAX_SAMPLES];
    private readonly int[] _network = new int[MAX_SAMPLES];
    private readonly int[] _pcProcess = new int[MAX_SAMPLES];
    private readonly int[] _buffer = new int[MAX_SAMPLES];
    private readonly int[] _renderer = new int[MAX_SAMPLES];
    private int _head, _count;
    private readonly object _lock = new();
    private System.Windows.Forms.Timer _timer;

    public LatencyChartPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = UiTheme.InputBg; // 浅色画布（与白色卡片协调）
        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (s, e) => Invalidate();
        _timer.Start();
    }

    public void AddSample(int total, int network, int pcProcess, int buffer, int renderer)
    {
        lock (_lock) {
            _total[_head] = total;
            _network[_head] = Math.Max(network, 0);
            _pcProcess[_head] = Math.Max(pcProcess, 0);
            _buffer[_head] = Math.Max(buffer, 0);
            _renderer[_head] = Math.Max(renderer, 0);
            _head = (_head + 1) % MAX_SAMPLES;
            if (_count < MAX_SAMPLES) _count++;
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

        int[] sTotal, sNet, sPc, sBuf, sRen;
        int snapCount;
        lock (_lock) {
            sTotal = new int[_count]; sNet = new int[_count]; sPc = new int[_count]; sBuf = new int[_count]; sRen = new int[_count];
            int start = (_head - _count + MAX_SAMPLES) % MAX_SAMPLES;
            for (int i = 0; i < _count; i++) {
                int idx = (start + i) % MAX_SAMPLES;
                sTotal[i] = _total[idx]; sNet[i] = _network[idx]; sPc[i] = _pcProcess[idx]; sBuf[i] = _buffer[idx]; sRen[i] = _renderer[idx];
            }
            snapCount = _count;
        }

        if (snapCount < 2) {
            using var nf = new Font("Microsoft YaHei UI", 9);
            using var nb = new SolidBrush(Color.FromArgb(150, 100, 116, 139));
            g.DrawString("等待数据...", nf, nb, w / 2 - 30, h / 2 - 8);
            return;
        }

        // 统计
        int minV = int.MaxValue, maxV = 0, sumV = 0;
        int sumNet = 0, sumPc = 0, sumBuf = 0, sumRen = 0;
        for (int i = 0; i < snapCount; i++) {
            if (sTotal[i] < minV) minV = sTotal[i];
            if (sTotal[i] > maxV) maxV = sTotal[i];
            sumV += sTotal[i]; sumNet += sNet[i]; sumPc += sPc[i]; sumBuf += sBuf[i]; sumRen += sRen[i];
        }
        int avgV = sumV / snapCount;
        int avgNet = sumNet / snapCount, avgPc = sumPc / snapCount, avgBuf = sumBuf / snapCount, avgRen = sumRen / snapCount;

        // Y轴范围
        int yMax = Math.Max(maxV + 10, 50);
        int yMin = Math.Max(minV - 5, 0);
        if (yMax - yMin < 20) yMax = yMin + 20;

        float padL = 0, padR = 0, padT = 18, padB = 4;
        float plotW = w - padL - padR;
        float plotH = h - padT - padB;

        // 网格线
        using var gridPen = new Pen(Color.FromArgb(60, 203, 213, 225), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        int[] gridValues = [50, 100, 200, 500];
        using var gridFont = new Font("Consolas", 7.5f);
        using var gridBrush = new SolidBrush(Color.FromArgb(150, 100, 116, 139));
        foreach (int gv in gridValues) {
            if (gv >= yMin && gv <= yMax) {
                float gy = padT + plotH * (1f - (float)(gv - yMin) / (yMax - yMin));
                g.DrawLine(gridPen, padL, gy, padL + plotW, gy);
                g.DrawString($"{gv}ms", gridFont, gridBrush, 2, gy - 10);
            }
        }

        float Y(int v) => padT + plotH * (1f - (float)(v - yMin) / (yMax - yMin));
        float X(int i) => padL + (float)i / (snapCount - 1) * plotW;

        // 堆叠区域: 从下到上: renderer(紫) → pcProcess(蓝) → network(绿) → buffer(橙) → 总线
        var baseRen = new float[snapCount];
        var topRen = new float[snapCount];
        var topPc = new float[snapCount];
        var topNet = new float[snapCount];
        var topBuf = new float[snapCount];

        for (int i = 0; i < snapCount; i++) {
            baseRen[i] = Y(0);
            topRen[i] = Y(sRen[i]);
            topPc[i] = Y(sRen[i] + sPc[i]);
            topNet[i] = Y(sRen[i] + sPc[i] + sNet[i]);
            topBuf[i] = Y(sRen[i] + sPc[i] + sNet[i] + sBuf[i]);
        }

        DrawStackedArea(g, snapCount, X, topNet, topBuf, Color.FromArgb(100, 255, 160, 60));   // buffer 橙
        DrawStackedArea(g, snapCount, X, topPc, topNet, Color.FromArgb(100, 60, 200, 120));    // network 绿
        DrawStackedArea(g, snapCount, X, topRen, topPc, Color.FromArgb(80, 80, 140, 255));     // pcProcess 蓝
        DrawStackedArea(g, snapCount, X, baseRen, topRen, Color.FromArgb(80, 180, 100, 220));   // renderer 紫

        // 总线
        var pts = new PointF[snapCount];
        for (int i = 0; i < snapCount; i++) pts[i] = new PointF(X(i), Y(sTotal[i]));
        using var linePen = new Pen(Color.FromArgb(220, 37, 99, 235), 1.5f);
        g.DrawLines(linePen, pts);

        // 图例 + 统计文字
        using var legFont = new Font("Consolas", 7.5f, FontStyle.Bold);
        float lx = w - 4;
        // 从右往左画
        string sAvg = $"avg {avgV}ms";
        var sAvgSize = g.MeasureString(sAvg, legFont);
        lx -= sAvgSize.Width;
        using var avgBrush = new SolidBrush(Color.FromArgb(220, 15, 23, 42));
        g.DrawString(sAvg, legFont, avgBrush, lx, 2);

        // 分项图例
        lx -= 8;
        DrawLegend(g, ref lx, $"缓冲 {avgBuf}ms", Color.FromArgb(200, 255, 160, 60), legFont);
        DrawLegend(g, ref lx, $"网络 {avgNet}ms", Color.FromArgb(200, 60, 200, 120), legFont);
        DrawLegend(g, ref lx, $"PC {avgPc}ms", Color.FromArgb(200, 80, 140, 255), legFont);
        DrawLegend(g, ref lx, $"渲染 {avgRen}ms", Color.FromArgb(200, 180, 100, 220), legFont);
    }

    private static void DrawStackedArea(Graphics g, int count, Func<int, float> X, float[] yTop, float[] yBottom, Color color)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddLine(X(0), yTop[0], X(0), yTop[0]); // 起点
        for (int i = 0; i < count; i++) path.AddLine(X(i), yTop[i], X(i), yTop[i]);
        for (int i = count - 1; i >= 0; i--) path.AddLine(X(i), yBottom[i], X(i), yBottom[i]);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static void DrawLegend(Graphics g, ref float lx, string text, Color color, Font font)
    {
        var size = g.MeasureString(text, font);
        lx -= size.Width + 14;
        using var dotBrush = new SolidBrush(color);
        g.FillRectangle(dotBrush, lx, 5, 8, 8);
        using var textBrush = new SolidBrush(Color.FromArgb(220, 71, 85, 105));
        g.DrawString(text, font, textBrush, lx + 10, 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer?.Stop(); _timer?.Dispose(); }
        base.Dispose(disposing);
    }
}

// ======================== 顶栏窗口控制按钮 ========================
// 最小化/关闭按钮（自绘符号 + hover 反馈），嵌入顶部导航栏
public class WinButton : Control {
    public enum BtnType { Minimize, Maximize, Close }
    public BtnType Type { get; set; } = BtnType.Minimize;
    private bool _hover;

    public WinButton() {
        Size = new Size(46, 32);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var fill = _hover ? (Type == BtnType.Close ? UiTheme.Danger : UiTheme.Hover) : Color.Transparent;
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, 0, 0, Width, Height);
        var penColor = _hover ? (Type == BtnType.Close ? Color.White : UiTheme.TextPrimary)
                              : Color.FromArgb(148, 163, 184);
        using var pen = new Pen(penColor, 1.4f);
        int cx = Width / 2;
        if (Type == BtnType.Minimize) {
            g.DrawLine(pen, cx - 5, Height / 2, cx + 5, Height / 2);
        } else if (Type == BtnType.Maximize) {
            g.DrawRectangle(pen, cx - 5, Height / 2 - 5, 10, 10);
        } else {
            g.DrawLine(pen, cx - 5, Height / 2 - 5, cx + 5, Height / 2 + 5);
            g.DrawLine(pen, cx + 5, Height / 2 - 5, cx - 5, Height / 2 + 5);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
}
