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

    /// <summary>电量数字/电池的强调色；正常电量用前景色本身。</summary>
    public static Color Accent(BatteryReading r, Color fore) => r.State != ReadState.Ok ? Color.FromArgb(140, fore)
        : r.Charging ? Color.FromArgb(76, 194, 255)
        : r.Percent <= 20 ? Color.FromArgb(255, 99, 97)
        : r.Percent <= 40 ? Color.FromArgb(255, 185, 0)
        : fore;

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
