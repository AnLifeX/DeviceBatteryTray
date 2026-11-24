using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace DeviceBatteryTray.Views;

/// <summary>
/// 电池图标：圆角外框 + 正极帽 + 按电量填充；充电时电池中间画闪电（和任务栏电量条的画法一致，见 Tray.Glyphs.DrawBattery）。
/// </summary>
public sealed class BatteryGlyph : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(double), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(Brushes.Green, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BoltProperty = DependencyProperty.Register(nameof(Bolt), typeof(Brush), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>闪电颜色（画在电量填充上，要和填充色有对比）。</summary>
    public Brush Bolt { get => (Brush)GetValue(BoltProperty); set => SetValue(BoltProperty, value); }

    protected override Size MeasureOverride(Size available) => new(26, 13);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 4) return;
        double cap = Math.Max(2, w * 0.08), stroke = Math.Max(1, h * 0.1);
        var body = new Rect(stroke / 2, stroke / 2, w - cap - stroke, h - stroke);
        double radius = h * 0.22;

        dc.DrawRoundedRectangle(null, new Pen(Stroke, stroke), body, radius, radius);
        dc.DrawRoundedRectangle(Stroke, null, new Rect(w - cap, h * 0.32, cap, h * 0.36), cap * 0.4, cap * 0.4);

        double inset = stroke + Math.Max(1, h * 0.08);
        var inner = new Rect(body.X + inset - stroke / 2, body.Y + inset - stroke / 2, body.Width - (inset - stroke / 2) * 2, body.Height - (inset - stroke / 2) * 2);
        double level = Math.Clamp(Percent, 0, 100) / 100;
        if (level > 0)
            dc.DrawRoundedRectangle(Fill, null, inner with { Width = Math.Max(inner.Height * 0.4, inner.Width * level) }, radius * 0.5, radius * 0.5);

        if (Charging) dc.DrawGeometry(Bolt, new Pen(Stroke, 0.6), BoltGeometry(body.X + body.Width / 2, body.Y + body.Height / 2, body.Height * 0.92));
    }

    /// <summary>闪电：与 Tray.Glyphs.BoltPoints 相同的比例。</summary>
    static Geometry BoltGeometry(double cx, double cy, double h)
    {
        var pts = Tray.Glyphs.BoltPoints((float)cx, (float)cy, (float)h);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), true, true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(new Point(pts[i].X, pts[i].Y), true, true);
        }
        g.Freeze();
        return g;
    }
}
