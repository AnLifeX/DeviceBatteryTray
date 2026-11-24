using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DeviceBatteryTray.Devices;
using DeviceBatteryTray.Ui;

namespace DeviceBatteryTray.Tray;

/// <summary>
/// 点击任务栏电量条弹出的面板（Win11 风格）：在线设备列表 + 底部「设置」「退出」。
/// 用分层窗口（UpdateLayeredWindow）整张位图推送，圆角和阴影自己画，Win10 上效果与 Win11 一致。
/// 点刷新不会关闭面板，数据回来后原地更新；点面板外或按 Esc 关闭。
/// </summary>
public sealed class Flyout : Form
{
    const int Width0 = 360;   // 内容宽（逻辑像素）
    const int ShadowPx = 16;  // 四周留给阴影的边距
    const int Gap = 12;       // 与任务栏 / 屏幕边缘的距离
    const int Radius = 8;
    static readonly string GlyphHome = ((char)0xE80F).ToString();
    static readonly TimeSpan EnterTime = TimeSpan.FromMilliseconds(180);
    static readonly TimeSpan MinSpin = TimeSpan.FromMilliseconds(700);

    public readonly record struct Hit(string Id, Rectangle Rect);

    private readonly IAppModel _m;
    private readonly List<Hit> _hits = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
    private readonly Stopwatch _enter = new();
    private float _s = 1;
    private Rectangle _wa;
    private bool _top;
    private int _anchorX;
    private Bitmap? _frame;
    private string? _hover, _pressed;
    private float _angle;
    private DateTime _spinUntil;

    public Flyout(IAppModel model)
    {
        _m = model;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;

        _m.Changed += OnModelChanged;
        _tick.Tick += (_, _) => OnTick();
        Deactivate += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        FormClosed += (_, _) =>
        {
            _m.Changed -= OnModelChanged;
            _tick.Dispose();
            _frame?.Dispose();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80; // WS_EX_LAYERED | WS_EX_TOOLWINDOW（不出现在 Alt+Tab）
            return cp;
        }
    }

    int S(float v) => (int)Math.Round(v * _s);

    /// <summary>贴着任务栏、靠近点击位置弹出（带上滑淡入）。</summary>
    public void ShowNear(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        _wa = screen.WorkingArea;
        _top = _wa.Top > screen.Bounds.Top; // 任务栏在顶部时从上往下弹
        _anchorX = anchor.X;
        _s = MonitorDpi(anchor) / 96f;

        CreateHandle();
        Render();
        Push(0);
        Show();
        Activate();
        SetForegroundWindow(Handle);
        _enter.Restart();
        _tick.Start();
    }

    private void OnModelChanged()
    {
        if (IsDisposed) return;
        if (_m.Refreshing) _tick.Start();
        Render();
        Push(255);
    }

    private bool Spinning => _m.Refreshing || DateTime.Now < _spinUntil;

    private void OnTick()
    {
        bool entering = _enter.IsRunning && _enter.Elapsed < EnterTime;
        if (Spinning)
        {
            _angle = (_angle + 360f * _tick.Interval / 800f) % 360;
            Render();
        }
        else if (_angle != 0) { _angle = 0; Render(); }

        Push(255);
        if (!entering) _enter.Stop();
        if (!entering && !Spinning) _tick.Stop();
    }

    // ---------- 位置与推送 ----------

    private void Render()
    {
        _frame?.Dispose();
        _hits.Clear();
        _frame = Render(_m, Theme.Current, _s, _hover, _pressed, _angle, _hits);
    }

    private void Push(byte alpha)
    {
        if (_frame == null || !IsHandleCreated) return;
        int margin = S(ShadowPx), w = _frame.Width - margin * 2, h = _frame.Height - margin * 2;
        int x = Math.Clamp(_anchorX - w / 2, _wa.Left + S(Gap), _wa.Right - w - S(Gap));
        int y = _top ? _wa.Top + S(Gap) : _wa.Bottom - h - S(Gap);

        // 进场动画：180ms 内从任务栏方向滑入 + 淡入（ease-out）
        if (_enter.IsRunning)
        {
            double t = Math.Min(1, _enter.Elapsed.TotalMilliseconds / EnterTime.TotalMilliseconds);
            double e = 1 - Math.Pow(1 - t, 3);
            y += (int)Math.Round((1 - e) * S(16) * (_top ? -1 : 1));
            alpha = (byte)Math.Round(255 * Math.Min(1, t * 1.6));
        }

        var bounds = new Rectangle(x - margin, y - margin, _frame.Width, _frame.Height);
        if (Bounds != bounds) Bounds = bounds;
        Layered.Push(Handle, _frame, bounds.Location, alpha);
    }

    // ---------- 鼠标 ----------

    private string? HitAt(Point p) => _hits.LastOrDefault(h => h.Rect.Contains(p)).Id;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitAt(e.Location);
        Cursor = h == null ? Cursors.Default : Cursors.Hand;
        if (h == _hover) return;
        _hover = h;
        Render(); Push(255);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover == null && _pressed == null) return;
        _hover = _pressed = null;
        Render(); Push(255);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _pressed = HitAt(e.Location);
        Render(); Push(255);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var id = HitAt(e.Location);
        bool click = id != null && id == _pressed;
        _pressed = null;
        Render(); Push(255);
        if (!click) return;

        switch (id)
        {
            case "refresh":
                _spinUntil = DateTime.Now + MinSpin;
                _tick.Start();
                _m.Refresh();
                break;
            case "settings": Close(); _m.ShowMain(MainPage.Settings); break;
            case "update": Close(); _m.ShowMain(MainPage.Settings); break;
            case "home":
            case var d when d!.StartsWith("dev:"):
                Close(); _m.ShowMain(MainPage.Home); break;
            case "exit": Close(); _m.Exit(); break;
        }
    }

    // ---------- 绘制 ----------

    /// <summary>画出整张面板（含阴影边距）。也用于 --dump 预览。</summary>
    public static Bitmap Render(IAppModel m, Theme t, float s, string? hover, string? pressed, float angle, List<Hit> hits)
    {
        int S(float v) => (int)Math.Round(v * s);
        var rows = m.Devices.Where(d => d.Online).ToList();
        var upd = m.Update;
        bool banner = upd.Status is UpdateStatus.Available or UpdateStatus.Downloading;

        int w = S(Width0), pad = S(20);
        int headerH = S(64), bannerH = banner ? S(52) : 0, rowH = S(60);
        int listH = rows.Count == 0 ? S(132) : rows.Count * rowH + S(8);
        int footerH = S(52);
        int h = headerH + bannerH + listH + footerH;
        int margin = S(ShadowPx);

        var bmp = new Bitmap(w + margin * 2, h + margin * 2, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        var card = new RectangleF(margin, margin, w, h);
        float rad = S(Radius);

        // 阴影：一圈圈外扩的半透明圆角矩形叠加，向下偏移 2px
        for (int i = margin; i >= 1; i--)
        {
            float k = 1 - i / (float)margin;
            Draw.Fill(g, Color.FromArgb((int)(t.Dark ? 18 * k * k : 11 * k * k), 0, 0, 0),
                RectangleF.Inflate(card with { Y = card.Y + S(2) }, i, i), rad + i);
        }

        using var cardPath = Draw.Round(card, rad);
        using (var bg = new SolidBrush(t.Surface)) g.FillPath(bg, cardPath);

        void Hit(string id, Rectangle r) => hits.Add(new Hit(id, r));
        Color StateBg(string id) => pressed == id ? t.Pressed : hover == id ? t.Hover : Color.Transparent;

        using var title = Draw.Font(15, s, bold: true);
        using var body = Draw.Font(14, s);
        using var small = Draw.Font(12, s);
        using var num = new Font(Draw.NumFont, 16 * s, GraphicsUnit.Pixel);
        using var icon = Draw.Icon(20, s);
        using var iconSmall = Draw.Icon(16, s);
        using var fore = new SolidBrush(t.Text);
        using var sub = new SolidBrush(t.TextSub);
        using var dim = new SolidBrush(t.TextDim);
        using var accent = new SolidBrush(t.Accent);

        int x0 = margin, y = margin;

        // 标题 + 更新时间 + 刷新按钮
        g.DrawString("设备电量", title, fore, new RectangleF(x0 + pad, y + S(12), w - S(100), S(22)), Draw.Left);
        string when = m.Refreshing ? "正在刷新…" : m.UpdatedAt is { } at ? $"{at:HH:mm} 更新" : "正在读取…";
        g.DrawString(when, small, sub, new RectangleF(x0 + pad, y + S(36), w - S(100), S(18)), Draw.Left);
        var refresh = new Rectangle(x0 + w - S(14) - S(36), y + S(14), S(36), S(36));
        Draw.Fill(g, StateBg("refresh"), refresh, S(4));
        DrawRotated(g, "\uE72C", iconSmall, fore, refresh, angle);
        Hit("refresh", refresh);
        y += headerH;

        // 新版本提示
        if (banner)
        {
            var r = new Rectangle(x0 + S(12), y, w - S(24), S(44));
            Draw.Fill(g, Color.FromArgb(t.Dark ? 38 : 28, t.Accent), r, S(6));
            if (hover == "update") Draw.Fill(g, t.Hover, r, S(6));
            g.DrawString("\uE896", iconSmall, accent, new RectangleF(r.X + S(10), r.Y, S(24), r.Height), Draw.Center);
            string text = upd.Status == UpdateStatus.Downloading
                ? $"正在更新到 v{upd.Info?.Version}… {upd.Progress}%"
                : $"发现新版本 v{upd.Info?.Version}";
            g.DrawString(text, body, fore, new RectangleF(r.X + S(40), r.Y, r.Width - S(100), r.Height), Draw.Left);
            if (upd.Status == UpdateStatus.Available)
                g.DrawString("查看", small, accent, new RectangleF(r.Right - S(60), r.Y, S(48), r.Height), Draw.Right);
            Hit("update", r);
            y += bannerH;
        }

        // 设备列表
        if (rows.Count == 0)
        {
            var r = new Rectangle(x0, y, w, listH);
            using var big = Draw.Icon(28, s);
            g.DrawString("\uE772", big, dim, new RectangleF(r.X, r.Y + S(14), w, S(36)), Draw.Center);
            g.DrawString("没有在线的设备", body, sub, new RectangleF(r.X, r.Y + S(54), w, S(22)), Draw.Center);
            var link = new Rectangle(x0 + w / 2 - S(60), r.Y + S(82), S(120), S(28));
            Draw.Fill(g, StateBg("home"), link, S(4));
            g.DrawString("打开主页面", small, accent, link, Draw.Center);
            Hit("home", link);
            y += listH;
        }
        else
        {
            y += S(4);
            foreach (var d in rows)
            {
                var rd = d.Reading!;
                var r = new Rectangle(x0 + S(6), y, w - S(12), rowH);
                string id = "dev:" + d.Id;
                Draw.Fill(g, StateBg(id), r, S(6));
                Hit(id, r);

                g.DrawString(Glyphs.Device(d.Kind), icon, fore, new RectangleF(x0 + pad - S(2), y, S(26), rowH), Draw.Center);
                int tx = x0 + pad + S(38), pctW = S(58);
                int textW = x0 + w - pad - pctW - tx;
                g.DrawString(d.Name, body, fore, new RectangleF(tx, y + S(9), textW, S(22)), Draw.Left);
                Draw.Bar(g, t, new RectangleF(tx, y + S(38), textW - S(10), S(4)), rd);

                bool low = !rd.Charging && rd.Percent <= 20;
                using var pctBrush = new SolidBrush(low ? t.Critical : t.Text);
                g.DrawString($"{rd.Percent}%", num, pctBrush, new RectangleF(x0 + w - pad - pctW, y + S(6), pctW, S(26)), Draw.Right);
                string? note = rd.Charging ? "充电中" : rd.Detail;
                if (note != null)
                    g.DrawString(note, small, sub, new RectangleF(x0 + w - pad - pctW - S(20), y + S(32), pctW + S(20), S(18)), Draw.Right);
                y += rowH;
            }
            y += S(4);
        }

        // 底栏：设置 / 退出
        var footer = new Rectangle(x0, y, w, footerH);
        g.SetClip(cardPath);
        using (var fb = new SolidBrush(t.SurfaceFooter)) g.FillRectangle(fb, footer);
        g.ResetClip();
        using (var line = new Pen(Color.FromArgb(t.Dark ? 60 : 40, t.Dark ? Color.White : Color.Black)))
            g.DrawLine(line, x0, y, x0 + w, y);

        int FooterButton(string id, string glyph, string text, int bx, bool alignRight)
        {
            float tw = Draw.TextWidth(g, text, body);
            int bw = S(16) + S(16) + S(8) + (int)tw + S(16);
            var r = new Rectangle(alignRight ? bx - bw : bx, y + S(8), bw, S(36));
            Draw.Fill(g, StateBg(id), r, S(4));
            g.DrawString(glyph, iconSmall, fore, new RectangleF(r.X + S(10), r.Y, S(24), r.Height), Draw.Center);
            g.DrawString(text, body, fore, new RectangleF(r.X + S(38), r.Y, tw + S(8), r.Height), Draw.Left);
            Hit(id, r);
            return r.Right;
        }
        // 左边：主页面、设置（直达主页面的设置页）；右边：退出
        int next = FooterButton("home", GlyphHome, "主页面", x0 + S(8), false);
        FooterButton("settings", "\uE713", "设置", next + S(4), false);
        FooterButton("exit", "\uE7E8", "退出", x0 + w - S(8), true);

        // 1px 描边（Win11 面板的细边框）
        Draw.Stroke(g, Color.FromArgb(t.Dark ? 70 : 30, t.Dark ? Color.White : Color.Black), card, rad);
        return bmp;
    }

    static void DrawRotated(Graphics g, string glyph, Font f, Brush b, Rectangle r, float angle)
    {
        var state = g.Save();
        g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        g.RotateTransform(angle);
        g.DrawString(glyph, f, b, new RectangleF(-r.Width / 2f, -r.Height / 2f, r.Width, r.Height), Draw.Center);
        g.Restore(state);
    }

    static uint MonitorDpi(Point p)
    {
        try
        {
            var mon = MonitorFromPoint(p, 2 /*MONITOR_DEFAULTTONEAREST*/);
            if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0) return dx;
        }
        catch { }
        return 96;
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
}

/// <summary>UpdateLayeredWindow 推送预乘 alpha 位图。</summary>
public static class Layered
{
    public static void Push(IntPtr hwnd, Bitmap bmp, Point pos, byte alpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = pos.X, y = pos.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old); DeleteObject(hBmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
}
