// IconGen：生成程序图标 app.ico（蓝色圆角底 + 白色电池 + 绿色电量）。
// 用法: IconGen <输出.ico> [预览.png]
// 每个尺寸单独绘制并对齐像素，小尺寸（16/20/24）不会糊。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

static class Program
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("IconGen <out.ico> [preview.png]"); return 1; }
        var images = Sizes.Select(Draw).ToList();
        WriteIco(args[0], images);
        if (args.Length > 1) WritePreview(args[1], images);
        Console.WriteLine($"写出 {args[0]}（{string.Join(",", Sizes)}）");
        return 0;
    }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Draw(int n)
    {
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = n / 256f;
        float R(float v) => MathF.Round(v * s); // 对齐到整像素

        // 底：圆角方块，上浅下深的蓝色渐变（小尺寸铺满，大尺寸留一点边）
        float inset = n <= 32 ? 0 : R(8);
        var tile = new RectangleF(inset, inset, n - inset * 2, n - inset * 2);
        using (var path = Round(tile, Math.Max(2, R(56))))
        using (var lg = new LinearGradientBrush(tile, Color.FromArgb(0x4C, 0x9B, 0xFF), Color.FromArgb(0x1A, 0x56, 0xD6), LinearGradientMode.Vertical))
            g.FillPath(lg, path);

        // 电池：白色外框 + 右侧正极帽 + 绿色电量（约 70%）
        bool small = n <= 24;
        float stroke = small ? (n <= 20 ? 1 : 2) : R(16);
        float bodyW = small ? MathF.Round(n * 0.68f) : R(150), bodyH = small ? MathF.Round(n * 0.5f) : R(100);
        float capW = small ? (n <= 20 ? 1 : 2) : R(16), capH = small ? MathF.Round(bodyH * 0.45f) : R(44);
        if (small) g.SmoothingMode = SmoothingMode.None; // 小尺寸关掉抗锯齿，线条落在整像素上
        float bx = MathF.Round((n - bodyW - capW) / 2), by = MathF.Round((n - bodyH) / 2);
        using (var white = new SolidBrush(Color.White))
        {
            if (small)
            {
                // 外框用四条实心矩形画，保证 1/2 像素宽的线不被拆到两列
                g.FillRectangle(white, bx, by, bodyW, stroke);
                g.FillRectangle(white, bx, by + bodyH - stroke, bodyW, stroke);
                g.FillRectangle(white, bx, by, stroke, bodyH);
                g.FillRectangle(white, bx + bodyW - stroke, by, stroke, bodyH);
            }
            else
            {
                var body = new RectangleF(bx + stroke / 2, by + stroke / 2, bodyW - stroke, bodyH - stroke);
                using var pen = new Pen(Color.White, stroke);
                using var path = Round(body, Math.Max(1, R(18)));
                g.DrawPath(pen, path);
            }
        }
        using (var white = new SolidBrush(Color.White))
        using (var cap = Round(new RectangleF(bx + bodyW, by + (bodyH - capH) / 2, capW, capH), Math.Max(1, R(6))))
            g.FillPath(white, cap);

        float gap = n <= 24 ? 1 : R(14);
        var inner = new RectangleF(bx + stroke + gap, by + stroke + gap, bodyW - (stroke + gap) * 2, bodyH - (stroke + gap) * 2);
        var level = inner with { Width = MathF.Round(inner.Width * 0.7f) };
        using (var green = new SolidBrush(Color.FromArgb(0x3D, 0xE0, 0x8A)))
        using (var path = Round(level, n <= 24 ? 0 : Math.Max(1, R(8))))
            g.FillPath(green, path);
        return bmp;
    }

    /// <summary>ICO：256 用 PNG，其余用 32 位 DIB（兼容性最好）。</summary>
    static void WriteIco(string file, List<Bitmap> images)
    {
        var blobs = images.Select(b => b.Width >= 256 ? Png(b) : Dib(b)).ToList();
        using var w = new BinaryWriter(File.Create(file));
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int n = images[i].Width;
            w.Write((byte)(n >= 256 ? 0 : n)); w.Write((byte)(n >= 256 ? 0 : n));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(blobs[i].Length); w.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (var b in blobs) w.Write(b);
    }

    static byte[] Png(Bitmap b) { using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray(); }

    static byte[] Dib(Bitmap b)
    {
        int n = b.Width;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(n); w.Write(n * 2); // 高度含 AND 掩码
        w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = n - 1; y >= 0; y--)
            for (int x = 0; x < n; x++)
            {
                var c = b.GetPixel(x, y);
                w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
            }
        int maskRow = (n + 31) / 32 * 4;
        w.Write(new byte[maskRow * n]); // 透明度由 alpha 通道决定，掩码全 0
        return ms.ToArray();
    }

    static void WritePreview(string file, List<Bitmap> images)
    {
        int pad = 12, scale = 4;
        int w = images.Sum(b => Math.Min(b.Width, 64) * (b.Width <= 32 ? scale : 1) + pad) + pad + 256 + pad;
        using var sheet = new Bitmap(w, 256 + pad * 2);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(40, 40, 40));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int x = pad;
        foreach (var b in images.Where(b => b.Width <= 64))
        {
            int k = b.Width <= 32 ? scale : 1;
            g.DrawImage(b, x, pad, b.Width * k, b.Height * k);
            x += b.Width * k + pad;
        }
        g.DrawImage(images[^1], x, pad, 256, 256);
        sheet.Save(file, ImageFormat.Png);
    }
}
