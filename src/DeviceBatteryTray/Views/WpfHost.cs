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

    /// <summary>按主题设置（见 Ui.ThemeMode）应用深浅色；还没手动切换过时跟随 Windows，系统切换会触发 UserPreferenceChanged。</summary>
    public static void ApplyTheme()
    {
        if (_app == null) return;
        ApplicationThemeManager.Apply(CurrentTheme, Backdrop, updateAccent: true);
    }

    /// <summary>
    /// 把窗口渲染成 PNG，供 --demo-ui 预览用。透明窗口（Win10 的主页面、弹出面板）渲染整个窗口，含圆角和阴影，和屏幕上看到的一致。
    /// transparent（README 截图）：透明背景；普通窗口（Win11）补上 8px 圆角。否则透明窗口垫一层灰色模拟桌面。
    /// </summary>
    public static void SaveSnapshot(System.Windows.Window w, string file, bool transparent = false)
    {
        // 透明窗口画整个窗口（包括模板里的背景），否则只画内容并补上主题背景
        System.Windows.Media.Visual visual = w.AllowsTransparency ? w : w.Content as System.Windows.FrameworkElement ?? (System.Windows.Media.Visual)w;
        var size = w.AllowsTransparency ? new System.Windows.Size(w.ActualWidth, w.ActualHeight)
            : ((System.Windows.FrameworkElement)visual).RenderSize;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var rect = new System.Windows.Rect(size);
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var appBg = (System.Windows.Media.Brush?)w.TryFindResource("ApplicationBackgroundBrush") ?? System.Windows.Media.Brushes.White;
            bool clip = transparent && !w.AllowsTransparency;
            if (!transparent)
                dc.DrawRectangle(w.AllowsTransparency ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x6A, 0x7A)) : appBg, null, rect);
            else if (clip)
            {
                dc.PushClip(new System.Windows.Media.RectangleGeometry(rect, 8, 8));
                dc.DrawRectangle(appBg, null, rect);
            }
            // 固定视口，避免 VisualBrush 按内容边界拉伸
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(visual)
            {
                ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute, Viewbox = rect, Stretch = System.Windows.Media.Stretch.None,
            }, null, rect);
            if (clip) dc.Pop();
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
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
