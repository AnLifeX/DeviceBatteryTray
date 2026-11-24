using System.Drawing;

namespace DeviceBatteryTray.Ui;

/// <summary>程序图标（内嵌资源 app.ico，由 tools/IconGen 生成）。</summary>
public static class AppIcon
{
    static byte[]? _bytes;

    static MemoryStream Open()
    {
        if (_bytes == null)
        {
            using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico")!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _bytes = ms.ToArray();
        }
        return new MemoryStream(_bytes, writable: false);
    }

    /// <summary>包含全部尺寸，给窗口用（系统自己挑大小图标）。</summary>
    public static Icon Full() { using var s = Open(); return new Icon(s); }

    public static Icon Sized(int px) { using var s = Open(); return new Icon(s, px, px); }

    public static Bitmap Bitmap(int px) { using var i = Sized(px); return i.ToBitmap(); }
}
