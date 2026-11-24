using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color; // 主程序还引用了 System.Drawing

namespace DeviceBatteryTray.Views;

/// <summary>
/// 程序的主色：图标电量环的青色，不跟随 Windows 强调色（主程序和安装程序共用）。
/// 浅色主题用深青（白字对比度 5.4:1），深色主题用亮青（配深色字）。
/// </summary>
public static class Accent
{
    /// <summary>主题颜色资源换好后调用（深浅色切换时也要调用）。</summary>
    public static void Apply(bool dark)
    {
        // 依次为：基准色、填充色（按钮 / 开关等）、强调文字、悬停 / 次要强调文字
        Color system = dark ? Rgb(0x06B6D4) : Rgb(0x0891B2);
        Color fill = dark ? Rgb(0x22D3EE) : Rgb(0x0E7490);
        Color strong = dark ? Rgb(0x67E8F9) : Rgb(0x155E75);
        // WPF-UI 按缓存的主题在 primary / secondary 之间选填充色，这里两者传同一个颜色，结果与缓存无关
        ApplicationAccentColorManager.Apply(system, fill, fill, strong);

        // 强调色的淡色底（新版本提示等），悬停时稍深
        var res = System.Windows.Application.Current.Resources;
        res["AccentSubtleBackgroundBrush"] = Brush(fill, dark ? 0x26 : 0x22);
        res["AccentSubtleBackgroundHoverBrush"] = Brush(fill, dark ? 0x38 : 0x33);
    }

    static Color Rgb(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    static SolidColorBrush Brush(Color c, int alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}
