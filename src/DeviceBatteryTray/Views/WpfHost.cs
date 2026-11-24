using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace DeviceBatteryTray.Views;

using DeviceBatteryTray.Ui;

/// <summary>
/// 在 WinForms 消息循环里使用 WPF 窗口：创建一个不运行的 WPF Application 来承载 WPF-UI 的主题资源，
/// 并让 WPF 窗口在 WinForms 消息循环下也能正常接收键盘输入。
/// </summary>
public static class WpfHost
{
    static System.Windows.Application? _app;

    public static void EnsureApp()
    {
        if (_app != null) return;
        _app = System.Windows.Application.Current ?? new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };
        // WPF 界面里的异常只记日志，不能让托盘程序跟着崩溃
        _app.DispatcherUnhandledException += (_, e) =>
        {
            Log.W("WPF 界面异常: " + e.Exception);
            e.Handled = true;
        };
        _app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = CurrentTheme });
        _app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        // 本程序的统一控件样式（圆角等），要放在 WPF-UI 之后才能 BasedOn 它的默认样式
        _app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DeviceBatteryTray;component/Views/Styles.xaml"),
        });
    }

    static ApplicationTheme CurrentTheme => Theme.Current.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;

    /// <summary>Win11 用 Mica 背景，Win10 不支持就用纯色。</summary>
    public static WindowBackdropType Backdrop =>
        Environment.OSVersion.Version.Build >= 22000 ? WindowBackdropType.Mica : WindowBackdropType.None;

    /// <summary>按「设置 → 主题」应用深浅色（跟随系统时也走这里，系统切换会触发 UserPreferenceChanged）。</summary>
    public static void ApplyTheme()
    {
        if (_app == null) return;
        ApplicationThemeManager.Apply(CurrentTheme, Backdrop, updateAccent: true);
    }

    /// <summary>
    /// 把窗口内容（含主题背景）渲染成 PNG，供 --demo-ui 预览用。
    /// transparent（README 截图）：透明背景，普通窗口裁成 8px 圆角，弹出面板保留阴影。
    /// </summary>
    public static void SaveSnapshot(System.Windows.Window w, string file, bool transparent = false)
    {
        if (w.Content is not System.Windows.FrameworkElement root) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        int pw = (int)(root.ActualWidth * dpi.DpiScaleX), ph = (int)(root.ActualHeight * dpi.DpiScaleY);
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rect = new System.Windows.Rect(0, 0, root.ActualWidth, root.ActualHeight);
            var appBg = (System.Windows.Media.Brush?)w.TryFindResource("ApplicationBackgroundBrush") ?? System.Windows.Media.Brushes.White;
            if (!transparent)
            {
                // 透明窗口（弹出面板）垫一层灰色，模拟桌面背景，阴影才看得出来
                var bg = w.AllowsTransparency ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x6A, 0x7A)) : appBg;
                dc.DrawRectangle(bg, null, rect);
            }
            else if (!w.AllowsTransparency)
            {
                dc.PushClip(new System.Windows.Media.RectangleGeometry(rect, 8, 8));
                dc.DrawRectangle(appBg, null, rect);
            }
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, rect);
            if (transparent && !w.AllowsTransparency) dc.Pop();
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    public static void Show(System.Windows.Window w)
    {
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(w);
        w.Show();
    }
}
