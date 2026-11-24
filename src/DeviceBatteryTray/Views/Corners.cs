using System;
using System.Windows;
using Control = System.Windows.Controls.Control;
using Border = System.Windows.Controls.Border;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using System.Windows.Media;

namespace DeviceBatteryTray.Views;

/// <summary>
/// 控件圆角。
/// <para>
/// WPF-UI 的部分控件（ComboBox、NumberBox、TextBox、CardAction）没有 CornerRadius 属性，
/// 模板里用的是 StaticResource 的 4px，改资源键也没用。这里等模板加载后，把模板里 4px 圆角的 Border 换成指定半径
/// （只改控件自己的模板，不进入 ContentPresenter，避免误改内容）。
/// </para>
/// 窗口圆角见 <see cref="ChromeWindow"/>。
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
}
