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
/// <para><see cref="RoundWindow"/>：Win10 的窗口没有系统圆角，用窗口区域裁出圆角（最大化时取消）。</para>
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

    /// <summary>Win11 由系统画圆角（抗锯齿 + 阴影），这里只在 Win10 上生效。</summary>
    public static bool NeedsWindowRegion => Environment.OSVersion.Version.Build < 22000;

    /// <summary>给窗口裁出 <paramref name="radius"/>（DIP）圆角，随窗口大小 / 最大化状态更新。</summary>
    public static void RoundWindow(Window w, double radius)
    {
        if (!NeedsWindowRegion) return;
        void Update()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (w.WindowState == WindowState.Maximized) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
            if (!GetWindowRect(hwnd, out var rc)) return;
            double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
            int d = (int)Math.Round(radius * 2 * scale);
            // CreateRoundRectRgn 的右下角不包含在内，所以宽高各 +1
            var rgn = CreateRoundRectRgn(0, 0, rc.Right - rc.Left + 1, rc.Bottom - rc.Top + 1, d, d);
            if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn); // 成功后区域归系统所有，不能再删
        }
        w.SourceInitialized += (_, _) => Update();
        w.SizeChanged += (_, _) => Update();
        w.StateChanged += (_, _) => Update();
        w.DpiChanged += (_, _) => Update();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
