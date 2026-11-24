using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Control = System.Windows.Controls.Control;
using Border = System.Windows.Controls.Border;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using System.Windows.Media;

namespace DeviceBatteryTray.Views;

/// <summary>
/// 圆角相关的小工具。
/// <para>
/// <see cref="RadiusProperty"/>：WPF-UI 的部分控件（ComboBox、NumberBox、TextBox、CardAction）没有 CornerRadius 属性，
/// 模板里用的是 StaticResource 的 4px，改资源键也没用。这里等模板加载后，把模板里 4px 圆角的 Border 换成指定半径
/// （只改控件自己的模板，不进入 ContentPresenter，避免误改内容）。
/// </para>
/// <para><see cref="RoundWindow"/>：Win10 的窗口没有系统圆角，改成透明窗口自己画抗锯齿的圆角和阴影（最大化时取消）。</para>
/// </summary>
public static class Corners
{
    /// <summary>WPF-UI 模板里的默认控件圆角。</summary>
    const double WpfUiRadius = 4;

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(Corners), new PropertyMetadata(double.NaN, OnRadiusChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double v) => o.SetValue(RadiusProperty, v);

    static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control c) return;
        c.Loaded -= OnLoaded;
        c.Loaded += OnLoaded;
        if (c.IsLoaded) Apply(c);
    }

    static void OnLoaded(object sender, RoutedEventArgs e) => Apply((Control)sender);

    static void Apply(Control c)
    {
        double r = GetRadius(c);
        if (double.IsNaN(r)) return;
        c.ApplyTemplate();
        Walk(c, r);
    }

    static void Walk(DependencyObject parent, double r)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(parent); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter) continue;
            if (child is Border b) b.CornerRadius = Scale(b.CornerRadius, r);
            Walk(child, r);
        }
    }

    /// <summary>只替换等于 WPF-UI 默认值的角（底部强调线之类的 0 / 特殊值保持不变）。</summary>
    static CornerRadius Scale(CornerRadius cr, double r)
    {
        static double S(double v, double r) => Math.Abs(v - WpfUiRadius) < 0.01 ? r : v;
        return new CornerRadius(S(cr.TopLeft, r), S(cr.TopRight, r), S(cr.BottomRight, r), S(cr.BottomLeft, r));
    }

    // ---------------- Win10 窗口圆角 ----------------

    /// <summary>Win11 由系统画圆角（抗锯齿 + 阴影），只有 Win10 需要自己画。</summary>
    public static bool NeedsSoftCorners => Environment.OSVersion.Version.Build < 22000;

    /// <summary>窗口四周留给阴影的透明边距（DIP）。</summary>
    const double ShadowMargin = 16;

    /// <summary>
    /// 在窗口类的静态构造函数里调用。FluentWindow 初始化时会把 WindowStyle 改回带边框，而透明窗口只允许 None（否则抛异常）；
    /// 这里替换 WindowStyle 的约束：透明窗口一律保持 None，FluentWindow 的修改被忽略，它的标题栏、拖动、缩放照常工作。
    /// </summary>
    public static void KeepBorderlessWhenTransparent(Type windowType)
    {
        Window.WindowStyleProperty.OverrideMetadata(windowType, new FrameworkPropertyMetadata(
            WindowStyle.SingleBorderWindow, null, (d, v) => d is Window { AllowsTransparency: true } ? WindowStyle.None : v));
        // FluentWindow 在 Win10 上还会加 1px 灰色边框，透明窗口里它会画在阴影最外圈；边框由 RoundWindow 的 frame 画
        Control.BorderThicknessProperty.OverrideMetadata(windowType, new FrameworkPropertyMetadata(
            new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (d, v) => d is Window { AllowsTransparency: true } ? new Thickness(0) : v));
        // WPF-UI 应用主题时会把窗口背景设回不透明的主题背景色，阴影那一圈就成了白底；背景由 RoundWindow 的 frame 画
        Control.BackgroundProperty.OverrideMetadata(windowType, new FrameworkPropertyMetadata(
            null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender,
            null, (d, v) => d is Window { AllowsTransparency: true } ? System.Windows.Media.Brushes.Transparent : v));
    }

    /// <summary>
    /// Win10 的窗口没有系统圆角。把窗口改成透明窗口，由 WPF 画出抗锯齿的圆角、细边框和阴影（和弹出面板的做法一样）；
    /// 最大化时去掉圆角和阴影。必须在窗口显示之前调用（构造函数里 InitializeComponent 之后）。
    /// 阴影画在内容后面单独的一层上：如果把阴影效果加在整个内容上，滚动等每次重绘都要重新计算阴影。
    /// </summary>
    public static void RoundWindow(Window w, double radius)
    {
        if (!NeedsSoftCorners || w.Content is not FrameworkElement content) return;

        w.Content = null;
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 3, Direction = 270, Opacity = 0.28 },
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        var frame = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), Child = content };
        frame.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        var root = new System.Windows.Controls.Grid { Margin = new Thickness(ShadowMargin) };
        root.Children.Add(shadow);
        root.Children.Add(frame);

        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Background = System.Windows.Media.Brushes.Transparent;
        w.Content = root;
        // 窗口变大一圈，内容区域保持原来的大小
        if (!double.IsNaN(w.Width)) w.Width += 2 * ShadowMargin;
        if (!double.IsNaN(w.Height)) w.Height += 2 * ShadowMargin;
        if (w.MinWidth > 0) w.MinWidth += 2 * ShadowMargin;
        if (w.MinHeight > 0) w.MinHeight += 2 * ShadowMargin;

        // 内容裁成圆角，标题栏等子元素的背景不会画到圆角外面
        void Layout()
        {
            bool max = w.WindowState == WindowState.Maximized;
            root.Margin = new Thickness(max ? 0 : ShadowMargin);
            shadow.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
            frame.CornerRadius = new CornerRadius(max ? 0 : radius);
            frame.BorderThickness = new Thickness(max ? 0 : 1);
            content.Clip = max ? null : new RectangleGeometry(new Rect(0, 0, content.ActualWidth, content.ActualHeight), radius - 1, radius - 1);
        }
        content.SizeChanged += (_, _) => Layout();
        w.StateChanged += (_, _) => Layout();

        // WPF-UI 在 Win10 上会把窗口合成目标的底色设成不透明的主题色（应用主题、切换深浅色时都会），
        // 透明窗口靠这个底色保持透明，被改掉后阴影那一圈就成了白底。加载后和每次主题变化后改回透明。
        void ClearCompositionBackground()
        {
            if (PresentationSource.FromVisual(w) is HwndSource { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;
        }
        Wpf.Ui.Appearance.ThemeChangedEvent onTheme = (_, _) => w.Dispatcher.BeginInvoke(new Action(ClearCompositionBackground),
            System.Windows.Threading.DispatcherPriority.Loaded);
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += onTheme;
        w.Closed += (_, _) => Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= onTheme;
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(new Action(ClearCompositionBackground), System.Windows.Threading.DispatcherPriority.Loaded);

        w.SourceInitialized += (_, _) =>
        {
            ClearCompositionBackground();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
            source?.AddHook(MaximizeToWorkArea); // 无边框窗口最大化时默认会盖住任务栏，改成只占工作区
            if (w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
                source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg != WM_NCHITTEST || w.WindowState != WindowState.Normal) return IntPtr.Zero;
                    int hit = ResizeHitTest(hwnd, lParam, VisualTreeHelper.GetDpi(w).DpiScaleX);
                    if (hit == 0) return IntPtr.Zero;
                    handled = true;
                    return new IntPtr(hit);
                });
        };
    }

    const int WM_GETMINMAXINFO = 0x0024, WM_NCHITTEST = 0x0084;

    /// <summary>
    /// 缩放边缘：内容边缘往外 6、往里 4 个 DIP（阴影最外圈完全透明，透明窗口在那里收不到鼠标，WindowChrome 自带的缩放边框用不上）。
    /// 返回 HT* 值，不在边缘上返回 0。
    /// </summary>
    static int ResizeHitTest(IntPtr hwnd, IntPtr lParam, double scale)
    {
        if (!GetWindowRect(hwnd, out var rc)) return 0;
        long lp = lParam.ToInt64();
        int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
        double m = ShadowMargin * scale, outside = 6 * scale, inside = 4 * scale, corner = 12 * scale;
        double l = rc.Left + m, t = rc.Top + m, r = rc.Right - m, b = rc.Bottom - m;
        if (x < l - outside || x > r + outside || y < t - outside || y > b + outside) return 0;
        bool onLeft = x < l + inside, onRight = x > r - inside, onTop = y < t + inside, onBottom = y > b - inside;
        // 角上的判定范围大一些，方便斜着拖
        bool nearLeft = x < l + corner, nearRight = x > r - corner, nearTop = y < t + corner, nearBottom = y > b - corner;
        if ((onTop && nearLeft) || (onLeft && nearTop)) return 13;       // HTTOPLEFT
        if ((onTop && nearRight) || (onRight && nearTop)) return 14;     // HTTOPRIGHT
        if ((onBottom && nearLeft) || (onLeft && nearBottom)) return 16; // HTBOTTOMLEFT
        if ((onBottom && nearRight) || (onRight && nearBottom)) return 17; // HTBOTTOMRIGHT
        if (onLeft) return 10;   // HTLEFT
        if (onRight) return 11;  // HTRIGHT
        if (onTop) return 12;    // HTTOP
        if (onBottom) return 15; // HTBOTTOM
        return 0;
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    static IntPtr MaximizeToWorkArea(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, false);
        return IntPtr.Zero; // 不设 handled，让 WPF 继续处理最小尺寸
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
