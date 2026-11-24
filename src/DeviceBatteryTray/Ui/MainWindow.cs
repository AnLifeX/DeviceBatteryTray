using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Ui;

/// <summary>
/// 主页面（双击 exe 打开）：左侧导航，右侧「设备」和「设置」两页。整页自绘，Win11 设置应用的样式。
/// 关闭窗口只是关掉页面，程序继续在任务栏上运行。
/// </summary>
public sealed class MainWindow : Form
{
    const int NavW = 240;
    const int ContentMaxW = 920;
    static readonly TimeSpan MinSpin = TimeSpan.FromMilliseconds(700);

    static string G(int code) => ((char)code).ToString();
    static readonly string GHome = G(0xE80F), GSetting = G(0xE713), GRefresh = G(0xE72C), GPower = G(0xE7E8),
        GSync = G(0xE895), GBattery = G(0xE856), GColor = G(0xE790), GDownload = G(0xE896), GInfo = G(0xE946),
        GDoc = G(0xE8A5), GGlobe = G(0xE774), GAdmin = G(0xE7EF), GChevron = G(0xE70D), GDevices = G(0xE772), GUser = G(0xE77B);

    private readonly IAppModel _m;
    private MainPage _page;
    private float _s = 1;
    private int _scroll, _contentH;
    private readonly List<(string Id, Rectangle R)> _hits = new();
    private string? _hover, _pressed;
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 16 };
    private float _angle;
    private DateTime _spinUntil;
    private bool? _darkApplied;

    public MainWindow(IAppModel model, MainPage page)
    {
        _m = model;
        _page = page;
        Text = "设备电量";
        Icon = AppIcon.Full();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        ResizeRedraw = true;
        _s = DeviceDpi / 96f;
        ClientSize = new Size(S(960), S(660));
        MinimumSize = new Size(S(780), S(520));
        BackColor = Theme.Current.WindowBg;

        _m.Changed += OnModelChanged;
        _spin.Tick += (_, _) => SpinTick();
        FormClosed += (_, _) => { _m.Changed -= OnModelChanged; _spin.Dispose(); };
    }

    public MainPage Page
    {
        get => _page;
        set { if (_page != value) { _page = value; _scroll = 0; Invalidate(); } }
    }

    /// <summary>界面缩放；--dump 预览时强制为 1。</summary>
    public float UiScale { get => _s; set => _s = value; }

    int S(float v) => (int)Math.Round(v * _s);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBar();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _s = e.DeviceDpiNew / 96f;
        MinimumSize = new Size(S(780), S(520));
        Invalidate();
    }

    private void OnModelChanged()
    {
        if (IsDisposed) return;
        if (_m.Refreshing) _spin.Start();
        ApplyTitleBar();
        BackColor = Theme.Current.WindowBg;
        Invalidate();
    }

    private void SpinTick()
    {
        if (_m.Refreshing || DateTime.Now < _spinUntil) _angle = (_angle + 360f * _spin.Interval / 800f) % 360;
        else { _angle = 0; _spin.Stop(); }
        Invalidate();
    }

    /// <summary>标题栏跟随深浅色（Win10 1809+ / Win11），Win11 上标题栏底色与页面一致。</summary>
    private void ApplyTitleBar()
    {
        if (!IsHandleCreated) return;
        var t = Theme.Current;
        if (_darkApplied == t.Dark) return;
        _darkApplied = t.Dark;
        int dark = t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref dark, 4);
        int caption = ColorTranslator.ToWin32(t.WindowBg), text = ColorTranslator.ToWin32(t.Text);
        DwmSetWindowAttribute(Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR（仅 Win11）
        DwmSetWindowAttribute(Handle, 36, ref text, 4);    // DWMWA_TEXT_COLOR（仅 Win11）
        if (Visible) { SendMessage(Handle, 0x86 /*WM_NCACTIVATE*/, 0, 0); SendMessage(Handle, 0x86, 1, 0); } // Win10 立即重画标题栏
    }

    // ---------- 绘制 ----------

    protected override void OnPaintBackground(PaintEventArgs e) { } // 全部在 OnPaint 里画，避免闪烁

    protected override void OnPaint(PaintEventArgs e) => PaintPage(e.Graphics, ClientSize);

    /// <summary>画整个页面；也用于 --dump 预览。</summary>
    public void PaintPage(Graphics g, Size client)
    {
        var t = Theme.Current;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(t.WindowBg);
        _hits.Clear();

        PaintNav(g, t, client);

        var area = new Rectangle(S(NavW), 0, client.Width - S(NavW), client.Height);
        g.SetClip(area);
        var state = g.Save();
        g.TranslateTransform(0, -_scroll);
        int x = area.X + S(4);
        int w = Math.Min(area.Width - S(4) - S(36), S(ContentMaxW));
        int bottom = _page == MainPage.Home ? PaintHome(g, t, x, w) : PaintSettings(g, t, x, w);
        g.Restore(state);
        g.ResetClip();

        _contentH = bottom + S(28);
        int max = Math.Max(0, _contentH - client.Height);
        if (_scroll > max) { _scroll = max; Invalidate(); }
        if (max > 0)
        {
            // 细滚动条指示
            float thumb = Math.Max(S(32), client.Height * (float)client.Height / _contentH);
            float ty = (client.Height - thumb - S(8)) * _scroll / max + S(4);
            Draw.Fill(g, Color.FromArgb(t.Dark ? 90 : 110, t.TextDim), new RectangleF(client.Width - S(7), ty, S(3), thumb), S(1.5f));
        }
    }

    void Hit(string id, Rectangle r, bool content = true)
    {
        if (content) r.Offset(0, -_scroll);
        _hits.Add((id, r));
    }

    Color StateBg(Theme t, string id) => _pressed == id ? t.Pressed : _hover == id ? t.Hover : Color.Transparent;

    private void PaintNav(Graphics g, Theme t, Size client)
    {
        using var title = Draw.Font(15, _s, bold: true);
        using var body = Draw.Font(14, _s);
        using var small = Draw.Font(12, _s);
        using var icon = Draw.Icon(16, _s);
        using var fore = new SolidBrush(t.Text);
        using var sub = new SolidBrush(t.TextSub);

        // 程序图标 + 名称 + 版本
        using (var bmp = AppIcon.Bitmap(S(32) <= 32 ? 32 : 48))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, new Rectangle(S(20), S(18), S(32), S(32)));
        }
        g.DrawString("设备电量", title, fore, new RectangleF(S(62), S(14), S(170), S(22)), Draw.Left);
        g.DrawString("v" + Updater.CurrentText, small, sub, new RectangleF(S(62), S(36), S(170), S(18)), Draw.Left);

        int y = S(76);
        foreach (var (page, glyph, text) in new[] { (MainPage.Home, GHome, "设备"), (MainPage.Settings, GSetting, "设置") })
        {
            string id = "nav:" + page;
            var r = new Rectangle(S(8), y, S(NavW) - S(16), S(38));
            bool sel = _page == page;
            Draw.Fill(g, sel ? (_hover == id ? Color.FromArgb(t.Hover.A * 2, t.Hover) : t.Hover) : StateBg(t, id), r, S(4));
            if (sel) Draw.Fill(g, t.Accent, new RectangleF(r.X, r.Y + (r.Height - S(16)) / 2f, S(3), S(16)), S(1.5f));
            g.DrawString(glyph, icon, fore, new RectangleF(r.X + S(12), r.Y, S(24), r.Height), Draw.Center);
            g.DrawString(text, body, fore, new RectangleF(r.X + S(46), r.Y, r.Width - S(50), r.Height), Draw.Left);
            Hit(id, r, content: false);
            y += S(42);
        }

        // 底部：权限状态
        bool admin = _m.Elevated;
        var pr = new RectangleF(S(20), client.Height - S(46), S(NavW) - S(28), S(30));
        using (var dimBrush = new SolidBrush(t.TextSub))
        {
            g.DrawString(admin ? GAdmin : GUser, icon, dimBrush, pr with { Width = S(20) }, Draw.Center);
            g.DrawString(admin ? "管理员权限" : "普通权限（读不到飞智黑武士）", small, dimBrush, pr with { X = pr.X + S(28), Width = pr.Width - S(28) }, Draw.Left);
        }
    }

    // ----- 设备页 -----

    private int PaintHome(Graphics g, Theme t, int x, int w)
    {
        using var h1 = Draw.Font(26, _s, bold: true);
        using var body = Draw.Font(14, _s);
        using var small = Draw.Font(12, _s);
        using var num = new Font(Draw.NumFont, 20 * _s, GraphicsUnit.Pixel);
        using var icon = Draw.Icon(24, _s);
        using var iconSmall = Draw.Icon(16, _s);
        using var fore = new SolidBrush(t.Text);
        using var sub = new SolidBrush(t.TextSub);
        using var dim = new SolidBrush(t.TextDim);

        int y = S(24);
        g.DrawString("设备", h1, fore, new RectangleF(x, y, S(300), S(40)), Draw.Left);

        // 右上角：更新时间 + 刷新
        var refresh = new Rectangle(x + w - S(40), y + S(2), S(36), S(36));
        Draw.Fill(g, StateBg(t, "refresh"), refresh, S(4));
        var st = g.Save();
        g.TranslateTransform(refresh.X + refresh.Width / 2f, refresh.Y + refresh.Height / 2f);
        g.RotateTransform(_angle);
        g.DrawString(GRefresh, iconSmall, fore, new RectangleF(-refresh.Width / 2f, -refresh.Height / 2f, refresh.Width, refresh.Height), Draw.Center);
        g.Restore(st);
        Hit("refresh", refresh);
        string when = _m.Refreshing ? "正在刷新…" : _m.UpdatedAt is { } at ? $"{at:HH:mm:ss} 更新" : "正在读取…";
        g.DrawString(when, small, sub, new RectangleF(refresh.X - S(208), refresh.Y, S(200), refresh.Height), Draw.Right);
        y += S(64);

        y = PaintUpdateBanner(g, t, x, w, y);

        foreach (var d in _m.Devices)
        {
            var r = new Rectangle(x, y, w, S(72));
            Card(g, t, r);
            var rd = d.Reading;
            bool online = d.Online;
            using (var ib = new SolidBrush(online ? t.Text : t.TextDim))
                g.DrawString(Tray.Glyphs.Device(d.Kind), icon, ib, new RectangleF(r.X + S(16), r.Y, S(36), r.Height), Draw.Center);
            int tx = r.X + S(64);
            // 电量条随窗口宽度伸缩（60~160），优先保证名称完整
            int barW = Math.Clamp(r.Width - S(64) - S(96) - S(260), S(60), S(160));
            int rightW = S(20) + S(64) + S(12) + barW + S(16);
            g.DrawString(d.Name, body, fore, new RectangleF(tx, r.Y + S(14), r.Width - (tx - r.X) - rightW, S(22)), Draw.Left);
            g.DrawString(StatusText(rd), small, sub, new RectangleF(tx, r.Y + S(38), r.Width - (tx - r.X) - rightW, S(18)), Draw.Left);

            if (online)
            {
                var pctRect = new RectangleF(r.Right - S(20) - S(64), r.Y, S(64), r.Height);
                bool low = !rd!.Charging && rd.Percent <= 20;
                using var pb = new SolidBrush(low ? t.Critical : t.Text);
                g.DrawString($"{rd.Percent}%", num, pb, pctRect, Draw.Right);
                Draw.Bar(g, t, new RectangleF(pctRect.X - S(12) - barW, r.Y + r.Height / 2f - S(2), barW, S(4)), rd);
            }
            else
            {
                g.DrawString(rd == null ? "…" : rd.State == ReadState.Offline ? "休眠" : "未连接", body, dim,
                    new RectangleF(r.Right - S(20) - S(120), r.Y, S(120), r.Height), Draw.Right);
            }
            y += S(76);
        }

        y += S(8);
        g.DrawString("任务栏只显示在线的设备。休眠或未连接的设备在下次刷新读到电量后会自动出现。", small, dim,
            new RectangleF(x + S(2), y, w, S(20)), Draw.Left);
        return y + S(20);
    }

    static string StatusText(BatteryReading? r) => r == null ? "正在读取…" : r.State switch
    {
        ReadState.Ok => string.Join(" · ", new[] { "已连接", r.Charging ? "充电中" : null, r.Detail }.Where(s => s != null)),
        ReadState.Offline => "接收器已连接，设备休眠或已关机",
        ReadState.NotFound => "没有检测到接收器",
        _ => r.Detail ?? "读取失败",
    };

    private int PaintUpdateBanner(Graphics g, Theme t, int x, int w, int y)
    {
        var u = _m.Update;
        if (u.Status is not (UpdateStatus.Available or UpdateStatus.Downloading)) return y;
        using var body = Draw.Font(14, _s);
        using var small = Draw.Font(12, _s);
        using var icon = Draw.Icon(20, _s);
        using var fore = new SolidBrush(t.Text);
        using var sub = new SolidBrush(t.TextSub);
        using var accent = new SolidBrush(t.Accent);

        var r = new Rectangle(x, y, w, S(68));
        Draw.Fill(g, Color.FromArgb(t.Dark ? 34 : 24, t.Accent), r, S(6));
        Draw.Stroke(g, Color.FromArgb(70, t.Accent), r, S(6));
        g.DrawString(GDownload, icon, accent, new RectangleF(r.X + S(16), r.Y, S(36), r.Height), Draw.Center);
        g.DrawString($"发现新版本 v{u.Info?.Version}", body, fore, new RectangleF(r.X + S(64), r.Y + S(13), r.Width - S(260), S(22)), Draw.Left);
        g.DrawString(u.Status == UpdateStatus.Downloading ? $"正在下载… {u.Progress}%" : "更新后程序会自动重启", small, sub,
            new RectangleF(r.X + S(64), r.Y + S(37), r.Width - S(260), S(18)), Draw.Left);
        if (u.Status == UpdateStatus.Available) Button(g, t, "banner-update", "立即更新", r, accentStyle: true);
        return y + S(76);
    }

    // ----- 设置页 -----

    private int PaintSettings(Graphics g, Theme t, int x, int w)
    {
        using var h1 = Draw.Font(26, _s, bold: true);
        using var fore = new SolidBrush(t.Text);
        int y = S(24);
        g.DrawString("设置", h1, fore, new RectangleF(x, y, S(300), S(40)), Draw.Left);
        y += S(64);

        var s = AppSettings.Current;
        y = Section(g, t, "常规", x, y);
        y = SettingCard(g, t, x, w, y, GPower, "开机自启",
            _m.Elevated ? "登录 Windows 后在后台启动（使用计划任务，不弹 UAC）" : "登录 Windows 后在后台启动",
            r => { Toggle(g, t, "autostart", r, _m.AutoStart); });
        y = SettingCard(g, t, x, w, y, GSync, "刷新间隔", "多久读取一次设备电量",
            r => Dropdown(g, t, "refresh-interval", r, AppSettings.RefreshText(s.RefreshSeconds)));
        y = SettingCard(g, t, x, w, y, GBattery, "低电量提醒", "电量低于该值时弹出通知（充电时不提醒）",
            r => Dropdown(g, t, "low-battery", r, AppSettings.LowBatteryText(s.LowBatteryPercent)));

        y = Section(g, t, "外观", x, y);
        y = SettingCard(g, t, x, w, y, GColor, "主题", "主页面和弹出面板的颜色；任务栏上的电量条始终跟随任务栏",
            r => Dropdown(g, t, "theme", r, AppSettings.ThemeText(s.Theme)));

        y = Section(g, t, "更新", x, y);
        y = SettingCard(g, t, x, w, y, GDownload, "自动更新", "每 6 小时检查一次 GitHub 上的新版本",
            r => Dropdown(g, t, "update-mode", r, AppSettings.UpdateText(s.Update)));
        var u = _m.Update;
        string desc = u.Status switch
        {
            UpdateStatus.Checking => "正在检查更新…",
            UpdateStatus.UpToDate => "已是最新版本",
            UpdateStatus.Available => $"发现新版本 v{u.Info?.Version}",
            UpdateStatus.Downloading => $"正在下载 v{u.Info?.Version}… {u.Progress}%",
            UpdateStatus.Failed => "检查更新失败：" + u.Error,
            _ => "还没有检查过更新",
        };
        y = SettingCard(g, t, x, w, y, GInfo, $"当前版本 v{Updater.CurrentText}", desc, r =>
        {
            if (u.Status == UpdateStatus.Available) Button(g, t, "update-action", "立即更新", r, accentStyle: true);
            else Button(g, t, "update-action", u.Status is UpdateStatus.Checking or UpdateStatus.Downloading ? "请稍候…" : "检查更新", r,
                enabled: u.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading));
        });

        y = Section(g, t, "关于", x, y);
        y = SettingCard(g, t, x, w, y, GDoc, "运行日志", Log.FilePath, r => Button(g, t, "open-log", "打开", r));
        y = SettingCard(g, t, x, w, y, GGlobe, "项目主页", Updater.RepoUrl, r => Button(g, t, "open-repo", "打开", r));
        return y;
    }

    int Section(Graphics g, Theme t, string text, int x, int y)
    {
        using var f = Draw.Font(14, _s, bold: true);
        using var b = new SolidBrush(t.Text);
        g.DrawString(text, f, b, new RectangleF(x + S(2), y + S(8), S(300), S(24)), Draw.Left);
        return y + S(40);
    }

    void Card(Graphics g, Theme t, Rectangle r)
    {
        Draw.Fill(g, t.Card, r, S(6));
        Draw.Stroke(g, t.CardBorder, r, S(6));
    }

    /// <summary>设置项卡片：左边图标、标题、说明，右边由 control 画控件（参数是控件可用区域）。</summary>
    int SettingCard(Graphics g, Theme t, int x, int w, int y, string glyph, string title, string desc, Action<Rectangle> control)
    {
        var r = new Rectangle(x, y, w, S(68));
        Card(g, t, r);
        using var icon = Draw.Icon(18, _s);
        using var body = Draw.Font(14, _s);
        using var small = Draw.Font(12, _s);
        using var fore = new SolidBrush(t.Text);
        using var sub = new SolidBrush(t.TextSub);
        g.DrawString(glyph, icon, fore, new RectangleF(r.X + S(16), r.Y, S(36), r.Height), Draw.Center);
        int tx = r.X + S(64), controlW = S(196);
        g.DrawString(title, body, fore, new RectangleF(tx, r.Y + S(13), r.Width - (tx - r.X) - controlW - S(16), S(22)), Draw.Left);
        g.DrawString(desc, small, sub, new RectangleF(tx, r.Y + S(36), r.Width - (tx - r.X) - controlW - S(16), S(18)), Draw.Left);
        control(new Rectangle(r.Right - S(20) - controlW, r.Y, controlW, r.Height));
        return y + S(72);
    }

    void Toggle(Graphics g, Theme t, string id, Rectangle area, bool on)
    {
        using var small = Draw.Font(14, _s);
        using var fore = new SolidBrush(t.Text);
        var sw = new RectangleF(area.Right - S(40), area.Y + (area.Height - S(20)) / 2f, S(40), S(20));
        g.DrawString(on ? "开" : "关", small, fore, new RectangleF(sw.X - S(48), area.Y, S(36), area.Height), Draw.Right);
        Draw.Toggle(g, t, sw, on, _hover == id, _s);
        Hit(id, new Rectangle((int)sw.X - S(56), area.Y + S(12), S(56) + (int)sw.Width + S(4), area.Height - S(24)));
    }

    void Dropdown(Graphics g, Theme t, string id, Rectangle area, string text)
    {
        var r = new Rectangle(area.Right - S(180), area.Y + (area.Height - S(32)) / 2, S(180), S(32));
        Draw.Fill(g, _hover == id ? t.ControlHover : t.Control, r, S(4));
        Draw.Stroke(g, t.ControlBorder, r, S(4));
        using var f = Draw.Font(13, _s);
        using var icon = Draw.Icon(10, _s);
        using var fore = new SolidBrush(t.Text);
        using var sub = new SolidBrush(t.TextSub);
        g.DrawString(text, f, fore, new RectangleF(r.X + S(12), r.Y, r.Width - S(40), r.Height), Draw.Left);
        g.DrawString(GChevron, icon, sub, new RectangleF(r.Right - S(32), r.Y, S(24), r.Height), Draw.Center);
        Hit(id, r);
    }

    void Button(Graphics g, Theme t, string id, string text, Rectangle area, bool accentStyle = false, bool enabled = true)
    {
        using var f = Draw.Font(13, _s);
        int bw = Math.Max(S(96), (int)Draw.TextWidth(g, text, f) + S(32));
        var r = new Rectangle(area.Right - bw, area.Y + (area.Height - S(32)) / 2, bw, S(32));
        bool hover = enabled && _hover == id;
        if (accentStyle)
            Draw.Fill(g, hover ? t.AccentHover : t.Accent, r, S(4));
        else
        {
            Draw.Fill(g, hover ? t.ControlHover : t.Control, r, S(4));
            Draw.Stroke(g, t.ControlBorder, r, S(4));
        }
        using var b = new SolidBrush(accentStyle ? t.OnAccent : enabled ? t.Text : t.TextDim);
        g.DrawString(text, f, b, r, Draw.Center);
        if (enabled) Hit(id, r);
    }

    // ---------- 交互 ----------

    private string? HitAt(Point p) => _hits.LastOrDefault(h => h.R.Contains(p)).Id;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitAt(e.Location);
        Cursor = h == null ? Cursors.Default : Cursors.Hand;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != null) { _hover = null; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _pressed = HitAt(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var id = HitAt(e.Location);
        bool click = id != null && id == _pressed;
        _pressed = null;
        Invalidate();
        if (click) OnClick(id!, _hits.Last(h => h.Id == id).R);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int max = Math.Max(0, _contentH - ClientSize.Height);
        int next = Math.Clamp(_scroll - e.Delta * S(64) / 120, 0, max);
        if (next != _scroll) { _scroll = next; Invalidate(); }
    }

    private void OnClick(string id, Rectangle r)
    {
        var s = AppSettings.Current;
        switch (id)
        {
            case "nav:Home": Page = MainPage.Home; break;
            case "nav:Settings": Page = MainPage.Settings; break;
            case "refresh":
                _spinUntil = DateTime.Now + MinSpin;
                _spin.Start();
                _m.Refresh();
                break;
            case "autostart":
                Cursor = Cursors.WaitCursor;
                _m.AutoStart = !_m.AutoStart;
                Cursor = Cursors.Default;
                break;
            case "refresh-interval":
                ThemedMenu.Show(this, r, AppSettings.RefreshChoices.Select(v =>
                    new ThemedMenu.Option(AppSettings.RefreshText(v), v == s.RefreshSeconds, () => AppSettings.Change(x => x.RefreshSeconds = v))), _s);
                break;
            case "low-battery":
                ThemedMenu.Show(this, r, AppSettings.LowBatteryChoices.Select(v =>
                    new ThemedMenu.Option(AppSettings.LowBatteryText(v), v == s.LowBatteryPercent, () => AppSettings.Change(x => x.LowBatteryPercent = v))), _s);
                break;
            case "theme":
                ThemedMenu.Show(this, r, Enum.GetValues<ThemeMode>().Select(v =>
                    new ThemedMenu.Option(AppSettings.ThemeText(v), v == s.Theme, () => AppSettings.Change(x => x.Theme = v))), _s);
                break;
            case "update-mode":
                ThemedMenu.Show(this, r, Enum.GetValues<UpdateMode>().Select(v =>
                    new ThemedMenu.Option(AppSettings.UpdateText(v), v == s.Update, () => AppSettings.Change(x => x.Update = v))), _s);
                break;
            case "update-action":
                if (_m.Update.Status == UpdateStatus.Available) _m.InstallUpdate();
                else _m.CheckUpdate();
                break;
            case "banner-update": _m.InstallUpdate(); break;
            case "open-log":
                if (File.Exists(Log.FilePath)) Process.Start("explorer.exe", $"/select,\"{Log.FilePath}\"");
                break;
            case "open-repo":
                Process.Start(new ProcessStartInfo(Updater.RepoUrl) { UseShellExecute = true });
                break;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int wp, int lp);
}
