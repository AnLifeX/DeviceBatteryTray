using System.Drawing;
using System.Drawing.Text;
using DeviceBatteryTray.Devices;
using Microsoft.Win32;

namespace DeviceBatteryTray.Tray;

/// <summary>Windows 自带 Segoe MDL2 Assets 图标字体里的设备 / 电池字形，风格与系统托盘一致。</summary>
public static class Glyphs
{
    public const string FontName = "Segoe MDL2 Assets";

    const char KeyboardClassic = '\uE765';
    const char Mouse = '\uE962';
    const char Game = '\uE7FC';
    const char Headphone = '\uE7F6';
    const char Devices = '\uE772';
    const char BatteryUnknown = '\uE996';
    // MDL2 电池字形不是连续的：Battery0..9 = E850..E859，Battery10 = E83F；
    // BatteryCharging0..8 = E85A..E862，BatteryCharging9 = E83E，BatteryCharging10 = EA93
    public const char Battery10 = '\uE83F';
    static char BatteryGlyph(int step) => step >= 10 ? Battery10 : (char)(0xE850 + step);
    static char ChargingGlyph(int step) => step >= 10 ? '\uEA93' : step == 9 ? '\uE83E' : (char)(0xE85A + step);

    public static string Device(DeviceKind k) => (k switch
    {
        DeviceKind.Keyboard => KeyboardClassic,
        DeviceKind.Mouse => Mouse,
        DeviceKind.Gamepad => Game,
        DeviceKind.Headset => Headphone,
        _ => Devices,
    }).ToString();

    public static string Battery(BatteryReading r)
    {
        if (r.State != ReadState.Ok) return BatteryUnknown.ToString();
        int step = Math.Clamp((int)Math.Round(r.Percent / 10.0), 0, 10);
        return (r.Charging ? ChargingGlyph(step) : BatteryGlyph(step)).ToString();
    }

    /// <summary>
    /// 电量数字/电池的强调色；正常电量用前景色本身。充电为绿色（电池字形本身带闪电），
    /// 深色任务栏用亮绿、浅色任务栏用深绿，保证对比度。
    /// </summary>
    public static Color Accent(BatteryReading r, Color fore) => r.State != ReadState.Ok ? Color.FromArgb(140, fore)
        : r.Charging ? (fore.GetBrightness() > 0.5f ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 123, 15))
        : r.Percent <= 20 ? Color.FromArgb(255, 99, 97)
        : r.Percent <= 40 ? Color.FromArgb(255, 185, 0)
        : fore;

    /// <summary>闪电多边形（中心 cx,cy，高 h），任务栏和 WPF 的电池图标共用。</summary>
    public static PointF[] BoltPoints(float cx, float cy, float h)
    {
        float w = h * 0.62f;
        return new[]
        {
            new PointF(cx + w * 0.10f, cy - h * 0.50f),
            new PointF(cx - w * 0.42f, cy + h * 0.08f),
            new PointF(cx - w * 0.02f, cy + h * 0.08f),
            new PointF(cx - w * 0.12f, cy + h * 0.50f),
            new PointF(cx + w * 0.42f, cy - h * 0.10f),
            new PointF(cx + w * 0.02f, cy - h * 0.10f),
        };
    }

    /// <summary>
    /// 画电池：圆角外框 + 正极帽 + 按电量填充（accent 色）；充电时电池中间画闪电（前景色，带一圈底色描边）。
    /// area 是电池占的区域（宽约为高的 1.9 倍时最好看）。
    /// </summary>
    public static void DrawBattery(Graphics g, RectangleF area, BatteryReading r, Color fore, Color accent, Color background)
    {
        float h = area.Height, w = area.Width;
        float cap = MathF.Max(1.5f, w * 0.08f), stroke = MathF.Max(1f, h * 0.11f);
        var body = new RectangleF(area.X + stroke / 2, area.Y + stroke / 2, w - cap - stroke, h - stroke);
        float radius = h * 0.22f;
        var outline = r.State == ReadState.Ok ? fore : Color.FromArgb(140, fore);
        using (var pen = new Pen(outline, stroke))
        using (var p = RoundRect(body, radius)) g.DrawPath(pen, p);
        using (var b = new SolidBrush(outline))
        using (var p = RoundRect(new RectangleF(area.Right - cap, area.Y + h * 0.32f, cap, h * 0.36f), cap * 0.4f)) g.FillPath(b, p);
        if (r.State != ReadState.Ok) return;

        float inset = stroke + MathF.Max(1f, h * 0.08f) - stroke / 2;
        var inner = new RectangleF(body.X + inset, body.Y + inset, body.Width - inset * 2, body.Height - inset * 2);
        float level = Math.Clamp(r.Percent, 0, 100) / 100f;
        if (level > 0)
            using (var b = new SolidBrush(accent))
            using (var p = RoundRect(inner with { Width = MathF.Max(inner.Height * 0.4f, inner.Width * level) }, radius * 0.5f)) g.FillPath(b, p);

        if (r.Charging)
        {
            var bolt = BoltPoints(body.X + body.Width / 2, body.Y + body.Height / 2, body.Height * 0.92f);
            using var edge = new Pen(background, MathF.Max(1f, h * 0.08f)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            using var fill = new SolidBrush(fore);
            g.DrawPolygon(edge, bolt);
            g.FillPolygon(fill, bolt);
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = MathF.Min(rad * 2, MathF.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static string Percent(BatteryReading r) => r.State switch
    {
        ReadState.Ok => $"{r.Percent}%",
        ReadState.Offline => "休眠",
        _ => "--",
    };

    /// <summary>任务栏/托盘是否深色（SystemUsesLightTheme = 0）。</summary>
    public static bool DarkTaskbar()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is not int v || v == 0;
    }

    public static Color TaskbarForeground() => DarkTaskbar() ? Color.White : Color.FromArgb(20, 20, 20);

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // 透明背景上不能用 ClearType
    }
}
