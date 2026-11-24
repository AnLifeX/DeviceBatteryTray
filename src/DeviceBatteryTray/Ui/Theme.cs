using System.Drawing;
using System.Drawing.Drawing2D;
using DeviceBatteryTray.Devices;
using Microsoft.Win32;

namespace DeviceBatteryTray.Ui;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// Win11 风格配色（参照 WinUI 3 的 Fluent 色板）。面板和主页面都是自己绘制的，
/// 所以 Win10 上的圆角、阴影、配色和 Win11 一致，不依赖系统的圆角 / Mica。
/// </summary>
public sealed class Theme
{
    public bool Dark { get; }
    public Color WindowBg { get; }     // 主页面背景
    public Color Card { get; }         // 卡片
    public Color CardBorder { get; }
    public Color Surface { get; }      // 弹出面板
    public Color SurfaceFooter { get; }
    public Color Border { get; }
    public Color Hover { get; }        // 列表项悬停（叠加在背景上）
    public Color Pressed { get; }
    public Color Control { get; }      // 按钮 / 下拉框
    public Color ControlHover { get; }
    public Color ControlBorder { get; }
    public Color Text { get; }
    public Color TextSub { get; }
    public Color TextDim { get; }
    public Color Track { get; }        // 电量条底色
    public Color Accent { get; }
    public Color AccentHover { get; }
    public Color OnAccent { get; }     // 强调色上的文字
    public Color Good { get; }
    public Color Caution { get; }
    public Color Critical { get; }

    Theme(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            WindowBg = C(0x20, 0x20, 0x20); Card = C(0x2B, 0x2B, 0x2B); CardBorder = C(0x1D, 0x1D, 0x1D);
            Surface = C(0x2C, 0x2C, 0x2C); SurfaceFooter = C(0x24, 0x24, 0x24); Border = C(0x45, 0x45, 0x45);
            Hover = Color.FromArgb(15, 255, 255, 255); Pressed = Color.FromArgb(10, 255, 255, 255);
            Control = C(0x37, 0x37, 0x37); ControlHover = C(0x3C, 0x3C, 0x3C); ControlBorder = C(0x45, 0x45, 0x45);
            Text = Color.White; TextSub = C(0xC5, 0xC5, 0xC5); TextDim = C(0x8A, 0x8A, 0x8A);
            Track = C(0x4A, 0x4A, 0x4A);
            Good = C(0x6C, 0xCB, 0x5F); Caution = C(0xFC, 0xE1, 0x00); Critical = C(0xFF, 0x99, 0xA4);
            OnAccent = Color.Black;
        }
        else
        {
            WindowBg = C(0xF3, 0xF3, 0xF3); Card = C(0xFB, 0xFB, 0xFB); CardBorder = C(0xE5, 0xE5, 0xE5);
            Surface = C(0xF9, 0xF9, 0xF9); SurfaceFooter = C(0xEE, 0xEE, 0xEE); Border = C(0xD5, 0xD5, 0xD5);
            Hover = Color.FromArgb(10, 0, 0, 0); Pressed = Color.FromArgb(6, 0, 0, 0);
            Control = C(0xFD, 0xFD, 0xFD); ControlHover = C(0xF6, 0xF6, 0xF6); ControlBorder = C(0xD8, 0xD8, 0xD8);
            Text = C(0x1A, 0x1A, 0x1A); TextSub = C(0x5C, 0x5C, 0x5C); TextDim = C(0x8E, 0x8E, 0x8E);
            Track = C(0xD6, 0xD6, 0xD6);
            Good = C(0x0F, 0x7B, 0x0F); Caution = C(0x9D, 0x5D, 0x00); Critical = C(0xC4, 0x2B, 0x1C);
            OnAccent = Color.White;
        }
        Accent = ReadAccent(dark);
        AccentHover = Color.FromArgb(230, Accent);
    }

    static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    private static Theme? _current;
    /// <summary>按设置（跟随系统 / 浅色 / 深色）得到的当前主题；系统或设置变化后调用 <see cref="Invalidate"/>。</summary>
    public static Theme Current => _current ??= new Theme(AppSettings.Current.Theme switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => SystemDark(),
    });

    public static Theme Of(bool dark) => new(dark);
    public static void Invalidate() => _current = null;

    /// <summary>应用的深浅色（设置 → 个性化 → 颜色 → 默认应用模式）。</summary>
    public static bool SystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 系统强调色。AccentPalette 是 8 个 RGBA：0..2 由浅到深的亮色、3 基色、4..6 暗色。
    /// 深色模式下 WinUI 用 Light2（索引 1），浅色模式用 Dark1（索引 4）。Win10 / Win11 都有这个值。
    /// </summary>
    static Color ReadAccent(bool dark)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                int i = (dark ? 1 : 4) * 4;
                return Color.FromArgb(p[i], p[i + 1], p[i + 2]);
            }
        }
        catch { }
        return dark ? C(0x60, 0xCD, 0xFF) : C(0x00, 0x5F, 0xB8);
    }

    /// <summary>电量颜色：充电用强调色，≤20% 红，≤40% 黄，其余绿。</summary>
    public Color Battery(BatteryReading r) =>
        r.Charging ? Accent : r.Percent <= 20 ? Critical : r.Percent <= 40 ? Caution : Good;
}

/// <summary>绘制小工具：圆角、开关、字体。</summary>
public static class Draw
{
    public const string UiFont = "Microsoft YaHei UI";
    public const string NumFont = "Segoe UI Semibold";

    public static Font Font(float px, float s, bool bold = false) =>
        new(UiFont, px * s, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    public static Font Icon(float px, float s) => new(Tray.Glyphs.FontName, px * s, GraphicsUnit.Pixel);

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, Color c, RectangleF r, float rad)
    {
        if (c.A == 0) return;
        using var b = new SolidBrush(c);
        using var p = Round(r, rad);
        g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, Color c, RectangleF r, float rad, float width = 1)
    {
        using var pen = new Pen(c, width);
        using var p = Round(new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width), rad);
        g.DrawPath(pen, p);
    }

    /// <summary>Win11 开关：40×20，开为强调色实心，关为描边。</summary>
    public static void Toggle(Graphics g, Theme t, RectangleF r, bool on, bool hover, float s)
    {
        float knob = (hover ? 14 : 12) * s;
        if (on)
        {
            Fill(g, hover ? t.AccentHover : t.Accent, r, r.Height / 2);
            using var b = new SolidBrush(t.OnAccent);
            g.FillEllipse(b, r.Right - r.Height / 2 - knob / 2, r.Y + (r.Height - knob) / 2, knob, knob);
        }
        else
        {
            if (hover) Fill(g, t.Hover, r, r.Height / 2);
            Stroke(g, t.TextSub, r, r.Height / 2, Math.Max(1, s));
            using var b = new SolidBrush(t.TextSub);
            g.FillEllipse(b, r.X + r.Height / 2 - knob / 2, r.Y + (r.Height - knob) / 2, knob, knob);
        }
    }

    /// <summary>电量条：圆头细条。</summary>
    public static void Bar(Graphics g, Theme t, RectangleF r, BatteryReading rd)
    {
        Fill(g, t.Track, r, r.Height / 2);
        if (rd.State != ReadState.Ok) return;
        float w = Math.Max(r.Height, r.Width * Math.Clamp(rd.Percent, 0, 100) / 100f);
        Fill(g, t.Battery(rd), r with { Width = w }, r.Height / 2);
    }

    public static readonly StringFormat Left = new() { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
    public static readonly StringFormat Right = new(Left) { Alignment = StringAlignment.Far };
    public static readonly StringFormat Center = new(Left) { Alignment = StringAlignment.Center };

    public static float TextWidth(Graphics g, string s, Font f) =>
        g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width;
}
