using Microsoft.Win32;

namespace DeviceBatteryTray.Ui;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// 当前深浅色：按「设置 → 主题」（跟随系统 / 浅色 / 深色）决定。主页面和弹出面板（WPF-UI）据此切换主题；
/// 任务栏电量条不看这里，始终跟随任务栏颜色（见 Glyphs.DarkTaskbar）。
/// </summary>
public sealed class Theme
{
    public bool Dark { get; }

    Theme(bool dark) => Dark = dark;

    private static Theme? _current;

    /// <summary>系统或设置变化后调用 <see cref="Invalidate"/> 重新计算。</summary>
    public static Theme Current => _current ??= new Theme(AppSettings.Current.Theme switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => SystemDark(),
    });

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
}
