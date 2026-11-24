using Microsoft.Win32;

namespace DeviceBatteryTray.Ui;

/// <summary>主页面和弹出面板的深浅色，由主页面的按钮切换。第一次运行时按 Windows 当前的深浅色初始化（见 AppSettings）。</summary>
public enum ThemeMode { Light, Dark }

/// <summary>
/// 当前深浅色。主页面和弹出面板（WPF-UI）据此切换主题；
/// 任务栏电量条不看这里，始终跟随任务栏颜色（见 Glyphs.DarkTaskbar）。
/// </summary>
public static class Theme
{
    public static bool Dark => AppSettings.Current.Theme == ThemeMode.Dark;

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
