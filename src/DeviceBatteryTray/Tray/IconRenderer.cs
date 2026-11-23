using System.Drawing;
using System.Runtime.InteropServices;
using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Tray;

/// <summary>程序兜底托盘图标（内嵌条显示不了时才出现）。</summary>
public static class IconRenderer
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    /// <summary>单个 MDL2 字形做成托盘图标（程序自身的图标用）。</summary>
    public static Icon RenderGlyph(string glyph)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            Glyphs.Prepare(g);
            g.Clear(Color.Transparent);
            using var font = new Font(Glyphs.FontName, size * 0.85f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Glyphs.TaskbarForeground());
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), fmt);
        }
        return ToIcon(bmp);
    }

    static Icon ToIcon(Bitmap bmp)
    {
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }
}
