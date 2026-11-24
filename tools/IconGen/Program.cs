// IconGen：生成程序图标 app.ico。
// 用法:
//   IconGen <输出.ico> [预览.png] [--design 名称]     生成图标（默认 ring）
//   IconGen --candidates <预览.png>                    把所有候选方案画在一张图上对比
// 每个尺寸单独绘制并对齐像素，小尺寸（16/20/24）不会糊。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

static class Program
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    static readonly Dictionary<string, (string Title, Action<Graphics, int> Draw)> Designs = new()
    {
        ["ring"] = ("A 环形电量", DrawRing),
        ["bars"] = ("B 多设备电量条", DrawBars),
        ["bolt"] = ("C 电池闪电", DrawBolt),
        ["classic"] = ("当前图标", DrawClassic),
    };

    static int Main(string[] args)
    {
        string design = "ring";
        int di = Array.IndexOf(args, "--design");
        if (di >= 0 && di + 1 < args.Length) { design = args[di + 1]; args = args.Where((_, i) => i != di && i != di + 1).ToArray(); }
        if (!Designs.ContainsKey(design)) { Console.WriteLine("未知方案: " + design); return 1; }

        if (args.Length == 2 && args[0] == "--candidates") { WriteCandidates(args[1]); return 0; }
        if (args.Length < 1) { Console.WriteLine("IconGen <out.ico> [preview.png] [--design ring|bars|bolt|classic] | --candidates <png>"); return 1; }

        var images = Sizes.Select(n => Render(design, n)).ToList();
        WriteIco(args[0], images);
        if (args.Length > 1) WritePreview(args[1], images);
        Console.WriteLine($"写出 {args[0]}（方案 {design}，{string.Join(",", Sizes)}）");
        return 0;
    }

    static Bitmap Render(string design, int n)
    {
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        Designs[design].Draw(g, n);
        return bmp;
    }

    // ---------- 公共 ----------

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>圆角方块底（小尺寸铺满，大尺寸留边，接近 Win11 应用图标的比例）。</summary>
    static RectangleF Tile(int n) { float inset = n <= 32 ? 0 : MathF.Round(n * 0.04f); return new RectangleF(inset, inset, n - inset * 2, n - inset * 2); }

    static void FillTile(Graphics g, int n, Color top, Color bottom, float angle = 90)
    {
        var t = Tile(n);
        using var path = Round(t, MathF.Max(2, t.Width * 0.23f));
        using var lg = new LinearGradientBrush(new RectangleF(t.X - 1, t.Y - 1, t.Width + 2, t.Height + 2), top, bottom, angle);
        g.FillPath(lg, path);
    }

    static PointF[] Bolt(float cx, float cy, float h)
    {
        float w = h * 0.62f;
        return new[]
        {
            new PointF(cx + w * 0.10f, cy - h * 0.50f),
            new PointF(cx - w * 0.42f, cy + h * 0.08f),
            new PointF(cx - w * 0.02f, cy + h * 0.08f),
            new PointF(cx - w * 0.12f, cy + h * 0.50f),
            new PointF(cx + w * 0.42f, cy - h * 0.10f),
            new PointF(cx + w * 0.02f, cy - h * 0.10f),
        };
    }

    // ---------- A 环形电量：深色底 + 绿到青的渐变圆环（约 75%）+ 中间闪电 ----------

    static void DrawRing(Graphics g, int n)
    {
        FillTile(g, n, Color.FromArgb(0x1E, 0x29, 0x3B), Color.FromArgb(0x0F, 0x17, 0x2A));
        float s = n / 256f;
        float stroke = n <= 16 ? 2.2f : n <= 24 ? 3f : n <= 32 ? 4f : 30 * s;
        float pad = n <= 24 ? n * 0.2f : 58 * s;
        var ring = new RectangleF(pad, pad, n - pad * 2, n - pad * 2);
        using (var track = new Pen(Color.FromArgb(55, 255, 255, 255), stroke))
            g.DrawEllipse(track, ring);
        using (var lg = new LinearGradientBrush(ring, Color.FromArgb(0x4A, 0xDE, 0x80), Color.FromArgb(0x22, 0xD3, 0xEE), 45f))
        using (var arc = new Pen(lg, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, ring, -90, 270);
        if (n >= 20)
        {
            float bh = ring.Height * (n <= 32 ? 0.62f : 0.56f);
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, Bolt(n / 2f, n / 2f, bh));
        }
    }

    // ---------- B 多设备电量条：紫色底 + 三条长短不一的电量条 ----------

    static void DrawBars(Graphics g, int n)
    {
        FillTile(g, n, Color.FromArgb(0x81, 0x8C, 0xF8), Color.FromArgb(0x4F, 0x46, 0xE5));
        bool small = n <= 24;
        float barH = small ? MathF.Max(2, MathF.Round(n * 0.14f)) : n * 0.13f;
        float gap = small ? MathF.Max(1, MathF.Round(n * 0.09f)) : n * 0.08f;
        float x = small ? MathF.Round(n * 0.18f) : n * 0.21f, w = n - x * 2;
        float y0 = MathF.Round((n - barH * 3 - gap * 2) / 2);
        float[] levels = { 0.9f, 0.6f, 0.32f };
        Color[] fills = { Color.FromArgb(0x86, 0xEF, 0xAC), Color.White, Color.FromArgb(0xFD, 0xE0, 0x47) };
        if (small) g.SmoothingMode = SmoothingMode.None;
        for (int i = 0; i < 3; i++)
        {
            var r = new RectangleF(x, y0 + i * (barH + gap), w, barH);
            using (var track = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            using (var p = Round(r, small ? 0 : barH / 2)) g.FillPath(track, p);
            using (var fill = new SolidBrush(fills[i]))
            using (var p = Round(r with { Width = MathF.Round(w * levels[i]) }, small ? 0 : barH / 2)) g.FillPath(fill, p);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
    }

    // ---------- C 电池闪电：青绿渐变底 + 白色实心电池 + 镂空闪电 ----------

    static void DrawBolt(Graphics g, int n)
    {
        FillTile(g, n, Color.FromArgb(0x34, 0xD3, 0x99), Color.FromArgb(0x06, 0x96, 0x8F), 60f);
        float s = n / 256f;
        bool small = n <= 24;
        float bw = small ? MathF.Round(n * 0.62f) : 150 * s, bh = small ? MathF.Round(n * 0.40f) : 92 * s, cap = small ? MathF.Max(1, MathF.Round(n * 0.07f)) : 14 * s;
        float bx = MathF.Round((n - bw - cap) / 2), by = MathF.Round((n - bh) / 2);
        var body = new RectangleF(bx, by, bw, bh);
        using var white = new SolidBrush(Color.White);
        using (var p = Round(body, small ? 1 : 22 * s)) g.FillPath(white, p);
        using (var p = Round(new RectangleF(bx + bw, by + bh * 0.3f, cap, bh * 0.4f), small ? 0 : 5 * s)) g.FillPath(white, p);
        using var hole = new SolidBrush(Color.FromArgb(0x05, 0x96, 0x69));
        g.FillPolygon(hole, Bolt(bx + bw / 2, by + bh / 2, bh * 0.78f));
    }

    // ---------- 当前图标（对比用） ----------

    static void DrawClassic(Graphics g, int n)
    {
        float s = n / 256f;
        float R(float v) => MathF.Round(v * s);
        float inset = n <= 32 ? 0 : R(8);
        var tile = new RectangleF(inset, inset, n - inset * 2, n - inset * 2);
        using (var path = Round(tile, Math.Max(2, R(56))))
        using (var lg = new LinearGradientBrush(tile, Color.FromArgb(0x4C, 0x9B, 0xFF), Color.FromArgb(0x1A, 0x56, 0xD6), LinearGradientMode.Vertical))
            g.FillPath(lg, path);
        bool small = n <= 24;
        float stroke = small ? (n <= 20 ? 1 : 2) : R(16);
        float bodyW = small ? MathF.Round(n * 0.68f) : R(150), bodyH = small ? MathF.Round(n * 0.5f) : R(100);
        float capW = small ? (n <= 20 ? 1 : 2) : R(16), capH = small ? MathF.Round(bodyH * 0.45f) : R(44);
        float bx = MathF.Round((n - bodyW - capW) / 2), by = MathF.Round((n - bodyH) / 2);
        using var white = new SolidBrush(Color.White);
        using (var pen = new Pen(Color.White, stroke))
        using (var path = Round(new RectangleF(bx + stroke / 2, by + stroke / 2, bodyW - stroke, bodyH - stroke), small ? 0 : R(18)))
            g.DrawPath(pen, path);
        g.FillRectangle(white, bx + bodyW, by + (bodyH - capH) / 2, capW, capH);
        float gap = small ? 1 : R(14);
        using var green = new SolidBrush(Color.FromArgb(0x3D, 0xE0, 0x8A));
        g.FillRectangle(green, bx + stroke + gap, by + stroke + gap, MathF.Round((bodyW - (stroke + gap) * 2) * 0.7f), bodyH - (stroke + gap) * 2);
    }

    // ---------- 输出 ----------

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
        w.Write(40); w.Write(n); w.Write(n * 2);
        w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = n - 1; y >= 0; y--)
            for (int x = 0; x < n; x++)
            {
                var c = b.GetPixel(x, y);
                w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
            }
        int maskRow = (n + 31) / 32 * 4;
        w.Write(new byte[maskRow * n]);
        return ms.ToArray();
    }

    static void WritePreview(string file, List<Bitmap> images)
    {
        int pad = 12, scale = 4;
        int w = images.Where(b => b.Width <= 64).Sum(b => b.Width * (b.Width <= 32 ? scale : 1) + pad) + pad + 256 + pad;
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

    /// <summary>所有方案对比：每行一个方案，256 大图 + 48/32/24/16 原尺寸，分别放在深色和浅色背景上。</summary>
    static void WriteCandidates(string file)
    {
        int rowH = 300, colW = 760;
        using var sheet = new Bitmap(colW * 2, rowH * Designs.Count);
        using var g = Graphics.FromImage(sheet);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
        int row = 0;
        foreach (var (key, (title, _)) in Designs)
        {
            foreach (var (bg, fg, col) in new[] { (Color.FromArgb(32, 32, 32), Color.White, 0), (Color.FromArgb(243, 243, 243), Color.Black, 1) })
            {
                int ox = col * colW, oy = row * rowH;
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, ox, oy, colW, rowH);
                using (var tb = new SolidBrush(fg)) g.DrawString(title, font, tb, ox + 20, oy + 12);
                using (var big = Render(key, 256)) g.DrawImage(big, ox + 20, oy + 40, 240, 240);
                int x = ox + 300;
                foreach (int n in new[] { 48, 32, 24, 16 })
                {
                    using var img = Render(key, n);
                    g.DrawImage(img, x, oy + 160 - n / 2, n, n); // 原尺寸（模拟任务栏 / 托盘里的大小）
                    x += n + 28;
                }
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                using (var img = Render(key, 24)) g.DrawImage(img, ox + 560, oy + 112, 96, 96); // 24px 放大 4 倍看像素
                g.InterpolationMode = InterpolationMode.Default;
            }
            row++;
        }
        sheet.Save(file, ImageFormat.Png);
        Console.WriteLine("写出 " + file);
    }
}
