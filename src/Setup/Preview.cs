#if DEBUG
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeviceBatteryTray.Setup;

/// <summary>
/// 开发用：--preview &lt;页面&gt; &lt;out.png&gt; 把安装程序的某个页面渲染成图片后退出，不执行任何安装操作。
/// 页面：welcome location options progress error done confirm uninstall-done update update-error
/// </summary>
static class Preview
{
    public static int Run(Application app, string page, string file)
    {
        Window w;
        if (page.StartsWith("update"))
        {
            var u = new UpdateWindow(Product.DefaultDir, 0, background: false) { NoRun = true };
            u.ShowPreview(page);
            w = u;
        }
        else
        {
            var wiz = page is "confirm" or "uninstall-done"
                ? new WizardWindow(WizardMode.Uninstall, Product.DefaultDir)
                : new WizardWindow(WizardMode.Install, null);
            wiz.ShowPreview(page);
            w = wiz;
        }
        w.ContentRendered += async (_, _) =>
        {
            await Task.Delay(800);
            Save(w, file);
            w.Close();
        };
        return app.Run(w);
    }

    /// <summary>
    /// 整个窗口渲染成 PNG（透明背景）。Win10 的透明窗口自带圆角和阴影；
    /// 普通窗口（Win11，圆角由系统画）这里补上 8px 圆角和背景。
    /// </summary>
    static void Save(Window w, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        var rect = new Rect(0, 0, w.ActualWidth, w.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (!w.AllowsTransparency)
            {
                dc.PushClip(new RectangleGeometry(rect, 8, 8));
                dc.DrawRectangle((Brush?)w.TryFindResource("ApplicationBackgroundBrush") ?? Brushes.White, null, rect);
            }
            // 固定视口，避免 VisualBrush 按内容边界拉伸
            dc.DrawRectangle(new VisualBrush(w) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect, Stretch = Stretch.None }, null, rect);
            if (!w.AllowsTransparency) dc.Pop();
        }
        var bmp = new RenderTargetBitmap((int)(rect.Width * dpi.DpiScaleX), (int)(rect.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
#endif
