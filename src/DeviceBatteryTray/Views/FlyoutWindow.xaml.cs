using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using DeviceBatteryTray.Ui;

namespace DeviceBatteryTray.Views;

/// <summary>
/// 点击任务栏电量条弹出的面板（WPF-UI 风格，和主页面一致）：在线设备 + 底部「主页面 / 设置 / 退出」。
/// 贴着任务栏弹出（上滑淡入），点面板外或按 Esc 收起（下滑淡出）。刷新不关闭面板，数据回来后原地更新。
/// </summary>
public partial class FlyoutWindow : Window
{
    const double GapDip = 12; // 卡片与任务栏 / 屏幕边缘的距离
    const double ShadowDip = 16; // 四周留给阴影的边距（与 XAML 里 Card 的 Margin 一致）

    private readonly IAppModel _m;
    private readonly MainViewModel _vm;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private System.Drawing.Point _anchor;
    private bool _top, _closing;

    public FlyoutWindow(IAppModel model)
    {
        _m = model;
        _vm = new MainViewModel(model);
        _vm.CloseRequested += () => Dismiss(animate: false);
        DataContext = _vm;
        InitializeComponent();

        _m.Changed += OnModelChanged;
        _tick.Tick += (_, _) => _vm.Tick();
        _tick.Start();
        Deactivated += (_, _) => Dismiss(animate: true);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(animate: true); };
        SizeChanged += (_, _) => { if (IsLoaded) Place(); }; // 设备增减时保持贴着任务栏
        Closed += (_, _) => { _m.Changed -= OnModelChanged; _tick.Stop(); };
    }

    void OnModelChanged() => _vm.Sync();

    /// <summary>在点击位置附近弹出。anchor 为屏幕像素坐标。</summary>
    public void ShowNear(System.Drawing.Point anchor)
    {
        _anchor = anchor;
        var screen = System.Windows.Forms.Screen.FromPoint(anchor);
        _top = screen.WorkingArea.Top > screen.Bounds.Top; // 任务栏在顶部时从上往下弹
        Left = -32000; Top = -32000; // 先放到屏幕外完成布局，拿到真实尺寸后再定位
        WpfHost.Show(this);
        Dispatcher.BeginInvoke(() =>
        {
            Place();
            Activate();
            PlayEnter();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>用像素坐标定位（WPF 的 Left/Top 在多显示器、不同缩放下换算麻烦）。</summary>
    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var wa = System.Windows.Forms.Screen.FromPoint(_anchor).WorkingArea;
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int margin = (int)Math.Round(ShadowDip * scale), gap = (int)Math.Round(GapDip * scale);
        int cardW = w - margin * 2;
        int x = Math.Clamp(_anchor.X - cardW / 2, wa.Left + gap, wa.Right - cardW - gap) - margin;
        int y = _top ? wa.Top + gap - margin : wa.Bottom - h + margin - gap;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    void PlayEnter()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(_top ? -16 : 16, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>收起：向任务栏方向滑出并淡出，然后关闭。</summary>
    public void Dismiss(bool animate)
    {
        if (_closing) return;
        _closing = true;
        if (!animate) { Close(); return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(0, _top ? -10 : 10, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
