using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
// 主程序同时引用了 WinForms 和 System.Drawing（隐式 using），这几个类型名要指明
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace DeviceBatteryTray.Views;

/// <summary>
/// 程序的窗口外框：主页面、安装向导、更新窗口共用（Setup 通过链接文件编译同一份代码）。
/// <para>Win10：透明窗口，自己画 8px 抗锯齿圆角、1px 边框和阴影（阴影单独一层，不加在内容上）；最大化时去掉圆角和阴影。</para>
/// <para>Win11：普通窗口，圆角、边框和阴影由系统画，背景为纯色。</para>
/// <para>
/// 标题栏：图标、标题、最小化 / 最大化 / 关闭。所有颜色都引用主题资源，切换深浅色只替换资源，
/// 窗口背景、合成底色在创建后不再改动。
/// </para>
/// </summary>
public class ChromeWindow : Window
{
    /// <summary>Win10 自绘的窗口圆角（与 Win11 系统圆角一致）。</summary>
    public const double Radius = 8;

    /// <summary>Win10 窗口四周留给阴影的透明边距（DIP）。</summary>
    const double ShadowMargin = 16;

    const double TitleBarHeight = 40;

    /// <summary>Win11 由系统画圆角和阴影，只有 Win10 需要自己画。</summary>
    public static bool SoftCorners { get; } = DetectSoftCorners();

    static bool DetectSoftCorners()
    {
#if DEBUG
        // 开发用：DEVICEBATTERYTRAY_CHROME=system 在 Win10 上走 Win11 的普通窗口分支，检查命中测试和最大化
        if (Environment.GetEnvironmentVariable("DEVICEBATTERYTRAY_CHROME") == "system") return false;
#endif
        return Environment.OSVersion.Version.Build < 22000;
    }

    public static readonly DependencyProperty ShowCloseProperty = DependencyProperty.Register(
        nameof(ShowClose), typeof(bool), typeof(ChromeWindow),
        new PropertyMetadata(true, (d, _) => ((ChromeWindow)d).UpdateButtons()));

    /// <summary>是否显示关闭按钮（安装进行中隐藏）。</summary>
    public bool ShowClose
    {
        get => (bool)GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

    /// <summary>跟踪主题背景色，Win11 上据此设置系统边框的深浅色。</summary>
    static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(
        "ThemeBackground", typeof(Brush), typeof(ChromeWindow),
        new PropertyMetadata(null, (d, _) => ((ChromeWindow)d).UpdateSystemDarkMode()));

    readonly Grid _root = new();
    readonly Border _shadow = new();
    readonly Border _frame = new();
    readonly Grid _body = new();
    readonly Grid _titleBar = new() { Height = TitleBarHeight };
    readonly CaptionButton _min, _max, _close;
    TextBlock _titleText = null!;

    public ChromeWindow()
    {
        _min = new CaptionButton(CaptionButton.Kind.Minimize, this);
        _max = new CaptionButton(CaptionButton.Kind.Maximize, this);
        _close = new CaptionButton(CaptionButton.Kind.Close, this);
        _min.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        _max.Click += (_, _) => ToggleMaximize();
        _close.Click += (_, _) => SystemCommands.CloseWindow(this);
        BuildTree();

        // 窗口模板只有一个容器，外框在 OnApplyTemplate 里放进去
        Template = new ControlTemplate(typeof(ChromeWindow)) { VisualTree = new FrameworkElementFactory(typeof(Border), "PART_Host") };
        Background = Brushes.Transparent;
        if (SoftCorners)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
        }
        // 去掉系统标题栏和边框，整个窗口都是客户区；拖动、缩放、最大化按钮的命中测试见 HitTest
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = default,
            // Win11：极小的非零值让系统保留窗口边框和阴影，又不画出玻璃区域
            GlassFrameThickness = SoftCorners ? new Thickness(0) : new Thickness(0.00001),
            UseAeroCaptionButtons = false,
        });
        SetResourceReference(ThemeBackgroundProperty, "ApplicationBackgroundBrush");
        // 内容继承的文字颜色和字号（与 WPF-UI 的窗口样式一致）
        SetResourceReference(ForegroundProperty, "WindowForeground");
        SetResourceReference(FontSizeProperty, "ControlContentThemeFontSize");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        StateChanged += (_, _) => { UpdateLayoutForState(); UpdateButtons(); };
        Activated += (_, _) => UpdateActive();
        Deactivated += (_, _) => UpdateActive();
    }

    void BuildTree()
    {
        // 标题栏：图标、标题、按钮
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(Icon)) { Source = this });
        var title = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        _titleText = title;
        Grid.SetColumn(title, 1);
        Grid.SetColumn(_min, 2);
        Grid.SetColumn(_max, 3);
        Grid.SetColumn(_close, 4);
        _titleBar.Children.Add(icon);
        _titleBar.Children.Add(title);
        _titleBar.Children.Add(_min);
        _titleBar.Children.Add(_max);
        _titleBar.Children.Add(_close);

        // 内容：AdornerDecorator 提供焦点框等装饰层（Window 默认模板里也有）
        var presenter = new ContentPresenter();
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(Content)) { Source = this });
        var adorner = new AdornerDecorator { Child = presenter };
        Grid.SetRow(adorner, 1);
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _body.Children.Add(_titleBar);
        _body.Children.Add(adorner);

        _frame.Child = _body;
        _frame.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        if (SoftCorners)
        {
            // 阴影单独一层画在内容后面：加在内容上的话，滚动等每次重绘都要重新计算阴影
            _shadow.CornerRadius = new CornerRadius(Radius);
            _shadow.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 3, Direction = 270, Opacity = 0.28 };
            _shadow.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
            _frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
            _root.Children.Add(_shadow);
            // 内容裁成圆角，标题栏等子元素的背景不会画到圆角外面
            _body.SizeChanged += (_, _) => UpdateClip();
        }
        _root.Children.Add(_frame);
        UpdateLayoutForState();
        UpdateActive();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Host") is Border host && host.Child != _root)
        {
            if (_root.Parent is Border old) old.Child = null;
            host.Child = _root;
        }
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        UpdateButtons();
        if (!SoftCorners) return;
        // 窗口四周加一圈阴影边距，内容区域保持 XAML 里写的大小
        double m = 2 * ShadowMargin;
        if (!double.IsNaN(Width)) Width += m;
        if (!double.IsNaN(Height)) Height += m;
        if (MinWidth > 0) MinWidth += m;
        if (MinHeight > 0) MinHeight += m;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ResizeModeProperty) UpdateButtons();
    }

    bool Maximized => WindowState == WindowState.Maximized;
    bool CanResizeWindow => ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

    void ToggleMaximize()
    {
        if (Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    void UpdateButtons()
    {
        if (_min is null) return; // 基类构造期间
        _min.Visibility = ResizeMode == ResizeMode.NoResize ? Visibility.Collapsed : Visibility.Visible;
        _max.Visibility = CanResizeWindow ? Visibility.Visible : Visibility.Collapsed;
        _close.Visibility = ShowClose ? Visibility.Visible : Visibility.Collapsed;
        _max.SetMaximized(Maximized);
    }

    void UpdateActive()
    {
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, IsActive ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        _min.UpdateLook();
        _max.UpdateLook();
        _close.UpdateLook();
    }

    /// <summary>Win10：最大化时去掉阴影边距、圆角和边框。</summary>
    void UpdateLayoutForState()
    {
        if (!SoftCorners) return;
        bool max = Maximized;
        _root.Margin = new Thickness(max ? 0 : ShadowMargin);
        _shadow.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        _frame.CornerRadius = new CornerRadius(max ? 0 : Radius);
        _frame.BorderThickness = new Thickness(max ? 0 : 1);
        UpdateClip();
    }

    void UpdateClip() =>
        _body.Clip = Maximized ? null : new RectangleGeometry(new Rect(0, 0, _body.ActualWidth, _body.ActualHeight), Radius - 1, Radius - 1);

    // ---------------- 窗口消息 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e); // WindowChrome 在这里挂上它的消息处理
        var hwnd = new WindowInteropHelper(this).Handle;
        // 后加的钩子先处理：命中测试完全由这里决定，WindowChrome 的不再生效
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        if (!SoftCorners)
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            UpdateSystemDarkMode();
        }
        FitToWorkArea(hwnd);
    }

    /// <summary>
    /// 窗口比所在显示器的工作区大时（小屏幕、高缩放），缩小到工作区以内并重新居中。
    /// 此时 WPF 已按 CenterScreen 定好位置（鼠标所在的显示器），窗口还没显示。
    /// </summary>
    void FitToWorkArea(IntPtr hwnd)
    {
        if (WindowState != WindowState.Normal || !GetWindowRect(hwnd, out var rc)) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/), ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double workW = (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX, workH = (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY;
        bool fit = false;
        if (rc.Right - rc.Left > info.rcWork.Right - info.rcWork.Left)
        {
            MinWidth = Math.Min(MinWidth, workW);
            Width = workW;
            fit = true;
        }
        if (rc.Bottom - rc.Top > info.rcWork.Bottom - info.rcWork.Top)
        {
            MinHeight = Math.Min(MinHeight, workH);
            if (SizeToContent == SizeToContent.Height || SizeToContent == SizeToContent.WidthAndHeight) MaxHeight = workH;
            else Height = workH;
            fit = true;
        }
        if (!fit || !GetWindowRect(hwnd, out rc)) return;
        int x = info.rcWork.Left + (info.rcWork.Right - info.rcWork.Left - (rc.Right - rc.Left)) / 2;
        int y = info.rcWork.Top + (info.rcWork.Bottom - info.rcWork.Top - (rc.Bottom - rc.Top)) / 2;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    /// <summary>Win11：系统边框按深浅色显示（DWM 属性，不涉及窗口背景）。</summary>
    void UpdateSystemDarkMode()
    {
        if (SoftCorners) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = GetValue(ThemeBackgroundProperty) is SolidColorBrush { Color: var c } && 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128 ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    const int WM_GETMINMAXINFO = 0x0024, WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0,
        WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCMOUSELEAVE = 0x02A2;
    const int HTCLIENT = 1, HTCAPTION = 2, HTMAXBUTTON = 9;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                int hit = HitTest(hwnd, lParam);
                _max.NcHover = hit == HTMAXBUTTON;
                handled = true;
                return new IntPtr(hit);
            // 最大化按钮返回 HTMAXBUTTON（Win11 悬停时显示贴靠布局），它收到的是非客户区消息，悬停和点击在这里处理
            case WM_NCMOUSEMOVE when wParam.ToInt32() == HTMAXBUTTON:
                var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = 0x12 /*TME_LEAVE | TME_NONCLIENT*/, hwndTrack = hwnd };
                TrackMouseEvent(ref track);
                break;
            case WM_NCMOUSELEAVE:
                _max.NcHover = false;
                _max.NcPressed = false;
                break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when wParam.ToInt32() == HTMAXBUTTON:
                _max.NcPressed = true;
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                bool click = _max.NcPressed;
                _max.NcPressed = false;
                handled = true;
                if (click) ToggleMaximize();
                break;
            // Win10 透明窗口没有标题栏样式，最大化时默认会盖住任务栏，改成只占工作区
            case WM_GETMINMAXINFO when SoftCorners:
                MaximizeToWorkArea(hwnd, lParam);
                break;
            // Win11 普通窗口最大化时，系统让窗口四周超出工作区一个边框宽度；整个窗口都是客户区，
            // 这里把客户区收回到工作区，超出的部分留在屏幕外
            case WM_NCCALCSIZE when !SoftCorners && wParam != IntPtr.Zero && IsZoomed(hwnd):
                var rc = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT))!;
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(MonitorFromRect(ref rc, 2 /*MONITOR_DEFAULTTONEAREST*/), ref info)) break;
                rc.Left = Math.Max(rc.Left, info.rcWork.Left);
                rc.Top = Math.Max(rc.Top, info.rcWork.Top);
                rc.Right = Math.Min(rc.Right, info.rcWork.Right);
                rc.Bottom = Math.Min(rc.Bottom, info.rcWork.Bottom);
                Marshal.StructureToPtr(rc, lParam, false);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>缩放边缘 → 最大化按钮 → 标题栏（拖动）→ 客户区。</summary>
    int HitTest(IntPtr hwnd, IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
        if (WindowState == WindowState.Normal && CanResizeWindow)
        {
            int edge = ResizeHitTest(hwnd, x, y, VisualTreeHelper.GetDpi(this).DpiScaleX);
            if (edge != 0) return edge;
        }
        var pt = new Point(x, y);
        if (IsOver(_max, pt)) return HTMAXBUTTON;
        if (IsOver(_min, pt) || IsOver(_close, pt)) return HTCLIENT;
        if (IsOver(_titleBar, pt)) return HTCAPTION;
        return HTCLIENT;
    }

    static bool IsOver(FrameworkElement el, Point screen)
    {
        if (!el.IsVisible || PresentationSource.FromVisual(el) == null) return false;
        var p = el.PointFromScreen(screen);
        return p.X >= 0 && p.Y >= 0 && p.X < el.ActualWidth && p.Y < el.ActualHeight;
    }

    /// <summary>
    /// 缩放边缘，返回 HT* 值，不在边缘上返回 0。
    /// Win10：内容边缘往外 6、往里 4 个 DIP（阴影最外圈完全透明，透明窗口在那里收不到鼠标）；Win11：窗口边缘往里 6 个 DIP。
    /// </summary>
    static int ResizeHitTest(IntPtr hwnd, int x, int y, double scale)
    {
        if (!GetWindowRect(hwnd, out var rc)) return 0;
        double m = (SoftCorners ? ShadowMargin : 0) * scale, outside = (SoftCorners ? 6 : 0) * scale,
            inside = (SoftCorners ? 4 : 6) * scale, corner = 12 * scale;
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

    static void MaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, false); // 不设 handled，让 WPF 继续处理最小尺寸
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, int flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------------- 标题栏按钮 ----------------

    /// <summary>
    /// 标题栏按钮：悬停 / 按下时变色，关闭按钮悬停时变红；窗口失去焦点时图标变淡。
    /// 最大化按钮在非客户区（HTMAXBUTTON），悬停 / 按下状态由窗口消息设置（NcHover / NcPressed）。
    /// </summary>
    sealed class CaptionButton : Button
    {
        public enum Kind { Minimize, Maximize, Close }

        static readonly Brush CloseHover = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
        static readonly Brush ClosePressed = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xC4, 0x2B, 0x1C)));

        readonly Kind _kind;
        readonly Window _owner;
        readonly TextBlock _glyph = new()
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        bool _ncHover, _ncPressed;

        public CaptionButton(Kind kind, Window owner)
        {
            _kind = kind;
            _owner = owner;
            Width = 46;
            Focusable = false;
            IsTabStop = false;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            Content = _glyph;
            _glyph.Text = kind switch { Kind.Minimize => "", Kind.Maximize => "", _ => "" };
            AutomationProperties.SetName(this, kind switch { Kind.Minimize => "最小化", Kind.Maximize => "最大化", _ => "关闭" });
            MouseEnter += (_, _) => UpdateLook();
            MouseLeave += (_, _) => UpdateLook();
            UpdateLook();
        }

        public bool NcHover { get => _ncHover; set { if (_ncHover == value) return; _ncHover = value; UpdateLook(); } }
        public bool NcPressed { get => _ncPressed; set { if (_ncPressed == value) return; _ncPressed = value; UpdateLook(); } }

        public void SetMaximized(bool maximized)
        {
            if (_kind != Kind.Maximize) return;
            _glyph.Text = maximized ? "" : "";
            AutomationProperties.SetName(this, maximized ? "还原" : "最大化");
        }

        protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnIsPressedChanged(e);
            UpdateLook();
        }

        public void UpdateLook()
        {
            bool hover = IsMouseOver || _ncHover, pressed = (IsPressed || _ncPressed) && hover;
            if (_kind == Kind.Close && hover)
            {
                Background = pressed ? ClosePressed : CloseHover;
                _glyph.Foreground = Brushes.White;
                return;
            }
            if (pressed) SetResourceReference(BackgroundProperty, "SubtleFillColorTertiaryBrush");
            else if (hover) SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
            else Background = Brushes.Transparent;
            _glyph.SetResourceReference(TextBlock.ForegroundProperty, _owner.IsActive || hover ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        }

        static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
