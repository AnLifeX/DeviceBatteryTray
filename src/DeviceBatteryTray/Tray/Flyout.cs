using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Tray;

/// <summary>
/// 点击任务栏电量条弹出的面板，仿 Win10 系统弹出面板（音量 / 网络）的样式：
/// 设备列表（图标、名称、电量条、百分比）+ 底部「开机自启」开关和「退出」。点面板外或按 Esc 关闭。
/// </summary>
public sealed class Flyout : Form
{
    public record Row(DeviceKind Kind, string Name, BatteryReading Reading);

    private readonly List<Row> _rows;
    private bool _autoStart;
    private readonly Action _refresh, _exit;
    private readonly Action<bool> _setAutoStart;
    private readonly float _s; // DPI 缩放
    private readonly bool _dark = Glyphs.DarkTaskbar();

    private Rectangle _refreshRect, _autoRect, _exitRect;
    private Rectangle? _hover;

    Color Bg => _dark ? Color.FromArgb(31, 31, 31) : Color.FromArgb(242, 242, 242);
    Color Border => _dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(204, 204, 204);
    Color Fore => _dark ? Color.White : Color.FromArgb(20, 20, 20);
    Color Sub => _dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(96, 96, 96);
    Color HoverBg => _dark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(218, 218, 218);
    Color Track => _dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(200, 200, 200);
    static readonly Color AccentBlue = Color.FromArgb(0, 120, 215);

    public Flyout(List<Row> rows, bool autoStart, Action refresh, Action<bool> setAutoStart, Action exit)
    {
        _rows = rows; _autoStart = autoStart; _refresh = refresh; _setAutoStart = setAutoStart; _exit = exit;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        _s = DeviceDpi / 96f;

        int w = S(320);
        int rowsH = Math.Max(1, _rows.Count) * S(56);
        Size = new Size(w, S(52) + rowsH + S(12) + S(1) + S(44) * 2 + S(8));

        Deactivate += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    int S(float v) => (int)Math.Round(v * _s);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
            return cp;
        }
    }

    /// <summary>贴着任务栏、靠近点击位置弹出。</summary>
    public void ShowNear(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Clamp(anchor.X - Width / 2, wa.Left + S(12), wa.Right - Width - S(12));
        Location = new Point(x, wa.Bottom - Height - S(12));
        Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Bg);
        using (var bp = new Pen(Border)) g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);

        using var title = new Font("Microsoft YaHei UI", 11f * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleBig = new Font("Microsoft YaHei UI", 15f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var body = new Font("Microsoft YaHei UI", 13f * _s, GraphicsUnit.Pixel);
        using var small = new Font("Microsoft YaHei UI", 11f * _s, GraphicsUnit.Pixel);
        using var pct = new Font("Segoe UI Semibold", 14f * _s, GraphicsUnit.Pixel);
        using var glyph = new Font(Glyphs.FontName, 20f * _s, GraphicsUnit.Pixel);
        using var glyphSmall = new Font(Glyphs.FontName, 14f * _s, GraphicsUnit.Pixel);
        using var fore = new SolidBrush(Fore);
        using var sub = new SolidBrush(Sub);
        var mid = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        var midRight = new StringFormat(mid) { Alignment = StringAlignment.Far };
        var midCenter = new StringFormat(mid) { Alignment = StringAlignment.Center };

        int pad = S(16);

        // 标题栏：「设备电量」+ 刷新按钮
        g.DrawString("设备电量", titleBig, fore, new RectangleF(pad, 0, Width - pad * 2, S(52)), mid);
        _refreshRect = new Rectangle(Width - S(16) - S(36), S(8), S(36), S(36));
        if (_hover == _refreshRect) Fill(g, HoverBg, _refreshRect);
        g.DrawString("\uE72C", glyphSmall, fore, _refreshRect, midCenter); // Refresh

        // 设备行
        int y = S(52);
        if (_rows.Count == 0)
        {
            g.DrawString("没有在线的设备", body, sub, new RectangleF(pad, y, Width - pad * 2, S(56)), mid);
            y += S(56);
        }
        foreach (var r in _rows)
        {
            var rd = r.Reading;
            var accent = Glyphs.Accent(rd, Fore) == Fore ? Color.FromArgb(16, 185, 129) : Glyphs.Accent(rd, Fore);
            g.DrawString(Glyphs.Device(r.Kind), glyph, fore, new RectangleF(pad, y, S(28), S(56)), midCenter);

            int textX = pad + S(40);
            int pctW = S(56);
            g.DrawString(r.Name, body, fore, new RectangleF(textX, y + S(8), Width - textX - pad - pctW, S(22)), mid);

            // 电量条
            var bar = new RectangleF(textX, y + S(36), Width - textX - pad - pctW - S(8), S(4));
            Fill(g, Track, Rectangle.Round(bar));
            Fill(g, accent, Rectangle.Round(bar with { Width = Math.Max(S(2), bar.Width * rd.Percent / 100f) }));

            g.DrawString($"{rd.Percent}%", pct, fore, new RectangleF(Width - pad - pctW, y + S(4), pctW, S(26)), midRight);
            string? note = rd.Charging ? "充电中" : rd.Detail;
            if (note != null)
                g.DrawString(note, small, sub, new RectangleF(Width - pad - pctW, y + S(30), pctW, S(18)), midRight);
            y += S(56);
        }

        // 分隔线
        y += S(12);
        using (var sp = new Pen(Border)) g.DrawLine(sp, pad, y, Width - pad, y);
        y += S(1) + S(4);

        // 开机自启（开关）
        _autoRect = new Rectangle(S(4), y, Width - S(8), S(44));
        if (_hover == _autoRect) Fill(g, HoverBg, _autoRect);
        g.DrawString("\uE7E8", glyphSmall, fore, new RectangleF(pad, y, S(28), S(44)), midCenter); // PowerButton
        g.DrawString("开机自启", body, fore, new RectangleF(pad + S(40), y, S(160), S(44)), mid);
        DrawToggle(g, new Rectangle(Width - pad - S(40), y + S(12), S(40), S(20)), _autoStart);
        y += S(44);

        // 退出
        _exitRect = new Rectangle(S(4), y, Width - S(8), S(44));
        if (_hover == _exitRect) Fill(g, HoverBg, _exitRect);
        g.DrawString("\uE711", glyphSmall, fore, new RectangleF(pad, y, S(28), S(44)), midCenter); // Cancel
        g.DrawString("退出", body, fore, new RectangleF(pad + S(40), y, S(160), S(44)), mid);
    }

    private void DrawToggle(Graphics g, Rectangle r, bool on)
    {
        using var path = Pill(r);
        if (on)
        {
            using var b = new SolidBrush(AccentBlue);
            g.FillPath(b, path);
        }
        else
        {
            using var p = new Pen(Fore, Math.Max(1f, _s));
            g.DrawPath(p, path);
        }
        int d = r.Height - S(10);
        int cx = on ? r.Right - S(5) - d : r.Left + S(5);
        using var knob = new SolidBrush(on ? Color.White : Fore);
        g.FillEllipse(knob, cx, r.Top + S(5), d, d);
    }

    static GraphicsPath Pill(Rectangle r)
    {
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, r.Height, r.Height, 90, 180);
        p.AddArc(r.Right - r.Height, r.Top, r.Height, r.Height, 270, 180);
        p.CloseFigure();
        return p;
    }

    static void Fill(Graphics g, Color c, Rectangle r) { using var b = new SolidBrush(c); g.FillRectangle(b, r); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Rectangle? h = new[] { _refreshRect, _autoRect, _exitRect }.Cast<Rectangle?>().FirstOrDefault(r => r!.Value.Contains(e.Location));
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h == null ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = null; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_refreshRect.Contains(e.Location)) { _refresh(); Close(); }
        else if (_autoRect.Contains(e.Location)) { _autoStart = !_autoStart; _setAutoStart(_autoStart); Invalidate(); }
        else if (_exitRect.Contains(e.Location)) { Close(); _exit(); }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}
