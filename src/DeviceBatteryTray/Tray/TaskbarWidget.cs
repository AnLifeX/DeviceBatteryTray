using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Tray;

/// <summary>
/// 嵌在任务栏里的电量条：每个设备「设备图标 + 电池格 + 百分比」，设备多时两行排列。
/// 做法与 TrafficMonitor 在 Win10 上相同：把任务按钮区（MSTaskSwWClass）缩窄，
/// 把本窗口作为分层子窗口挂进 ReBarWindow32 的右侧空出来的位置。退出时恢复。
/// </summary>
public sealed class TaskbarWidget : Form
{
    public record Item(DeviceKind Kind, string Name, BatteryReading Reading);

    private IntPtr _tray, _rebar, _taskSw;
    private Rectangle _taskSwOrig;     // 任务按钮区原始位置（相对 rebar）
    private int _width;
    private List<Item> _items = new();
    private readonly System.Windows.Forms.Timer _guard = new() { Interval = 1000 };

    public event Action<Point>? Clicked;

    public TaskbarWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1, 1);
        _guard.Tick += (_, _) => EnsureLayout();
        MouseUp += (_, e) => Clicked?.Invoke(Cursor.Position);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // 先以弹出窗口创建（WS_CHILD 必须有父窗口），挂进任务栏后再改成子窗口样式
            cp.Style = unchecked((int)WS_POPUP);
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// 右键也用来打开面板：子窗口没处理的 WM_CONTEXTMENU 会被转给父窗口（任务栏），弹出任务栏自己的右键菜单
    /// 并抢走焦点，所以这里吞掉它。左右键的 MouseUp 都会触发 Clicked。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x007B /*WM_CONTEXTMENU*/) return;
        base.WndProc(ref m);
    }

    /// <summary>更新显示；返回内嵌条此刻是否真的显示在任务栏上。</summary>
    public bool Update(List<Item> items)
    {
        _items = items;
        if (!Attach()) { _rebar = _taskSw = IntPtr.Zero; return false; }
        Redraw();
        return _width > 0;
    }

    // ---------- 挂接任务栏 ----------

    private bool Attach()
    {
        if (_rebar != IntPtr.Zero && IsWindow(_rebar) && IsWindow(_taskSw)) return true;

        _tray = FindWindow("Shell_TrayWnd", null);
        _rebar = FindWindowEx(_tray, IntPtr.Zero, "ReBarWindow32", null);
        _taskSw = FindWindowEx(_rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (_tray == IntPtr.Zero || _rebar == IntPtr.Zero || _taskSw == IntPtr.Zero) return false;

        GetWindowRect(_rebar, out var rb);
        if (rb.Bottom - rb.Top > rb.Right - rb.Left) return false; // 竖向任务栏暂不支持

        GetWindowRect(_taskSw, out var ts);
        _taskSwOrig = new Rectangle(ts.Left - rb.Left, ts.Top - rb.Top, ts.Right - ts.Left, ts.Bottom - ts.Top);

        if (!IsHandleCreated) CreateHandle();
        SetWindowLong(Handle, GWL_STYLE, unchecked((int)(WS_CHILD | WS_CLIPSIBLINGS)));
        if (SetParent(Handle, _rebar) == IntPtr.Zero && GetParent(Handle) != _rebar) return false;
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        _guard.Start();
        return true;
    }

    /// <summary>Explorer 会在窗口增减时重排任务按钮区，定时把它缩回去。</summary>
    private void EnsureLayout()
    {
        if (!IsWindow(_rebar) || !IsWindow(_taskSw)) { _rebar = IntPtr.Zero; Update(_items); return; } // Explorer 重启
        if (_width <= 0) return;
        GetWindowRect(_rebar, out var rb);
        GetWindowRect(_taskSw, out var ts);
        int rebarW = rb.Right - rb.Left;
        int want = rebarW - _taskSwOrig.Left - _width;
        if (ts.Right - ts.Left != want)
        {
            _taskSwOrig.Height = rb.Bottom - rb.Top;
            MoveWindow(_taskSw, _taskSwOrig.Left, 0, Math.Max(0, want), rb.Bottom - rb.Top, true);
        }
        SetWindowPos(Handle, HWND_TOP, rebarW - _width, 0, _width, rb.Bottom - rb.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// 不依赖实例的任务栏还原：程序被强制结束（如安装包关不掉它时）后，
    /// 任务按钮区仍是缩窄的宽度。它的左边位置没被改过，把宽度撑满到 rebar 右侧即可。
    /// </summary>
    public static void RestoreTaskbarLayout()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        var rebar = FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
        var taskSw = FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (rebar == IntPtr.Zero || taskSw == IntPtr.Zero) return;
        GetWindowRect(rebar, out var rb);
        GetWindowRect(taskSw, out var ts);
        if (rb.Bottom - rb.Top > rb.Right - rb.Left) return; // 竖向任务栏没有改过
        int left = ts.Left - rb.Left;
        MoveWindow(taskSw, left, 0, rb.Right - rb.Left - left, rb.Bottom - rb.Top, true);
    }

    public void Detach()
    {
        _guard.Stop();
        if (IsWindow(_taskSw) && IsWindow(_rebar))
        {
            GetWindowRect(_rebar, out var rb);
            MoveWindow(_taskSw, _taskSwOrig.Left, 0, rb.Right - rb.Left - _taskSwOrig.Left, rb.Bottom - rb.Top, true);
        }
        if (IsHandleCreated)
        {
            ShowWindow(Handle, SW_HIDE);
            SetParent(Handle, IntPtr.Zero);
            SetWindowLong(Handle, GWL_STYLE, unchecked((int)WS_POPUP));
        }
        _rebar = _taskSw = IntPtr.Zero;
        _width = 0;
    }

    // ---------- 绘制 ----------

    private void Redraw()
    {
        GetWindowRect(_rebar, out var rb);
        if (_items.Count == 0) { _width = 0; EnsureLayoutHidden(); return; }

        using var bmp = Render(_items, rb.Bottom - rb.Top, Glyphs.TaskbarForeground());
        _width = bmp.Width;
        EnsureLayout();
        Push(bmp);
    }

    /// <summary>任务栏上最多显示的格数（两行四列）；设备更多时最后一格显示「+N」，完整列表看弹出面板。</summary>
    public const int MaxCells = 8;

    /// <summary>画出内嵌条位图（高度 = 任务栏高度）。也用于 --dump 预览。</summary>
    public static Bitmap Render(List<Item> shown, int h, Color fore)
    {
        int more = shown.Count > MaxCells ? shown.Count - (MaxCells - 1) : 0;
        int cells = Math.Min(shown.Count, MaxCells);
        float scale = h / 40f; // Win10 100% 缩放时任务栏高 40px

        using var glyphFont = new Font(Glyphs.FontName, 14f * scale, GraphicsUnit.Pixel);
        using var textFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

        bool twoRows = cells > 1;
        int rows = twoRows ? 2 : 1;
        float rowH = h / (float)rows;
        float gap = 3 * scale, cellPad = 10 * scale, edge = 6 * scale;
        float glyphW = 16 * scale, battW = 18 * scale;

        // 量出每个单元宽度，按列排布（第 i 个在第 i/rows 列）
        using var measureBmp = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measureBmp);
        float TextW(string s) => mg.MeasureString(s, textFont, PointF.Empty, StringFormat.GenericTypographic).Width;
        float textMax = TextW("100%");
        float cellW = glyphW + gap + battW + gap + textMax + 4 * scale;
        int cols = (cells + rows - 1) / rows;
        int width = (int)Math.Ceiling(edge * 2 + cols * cellW + (cols - 1) * cellPad);

        var bmp = new Bitmap(width, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Glyphs.Prepare(g);
            g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha=1：几乎透明，但能接收鼠标点击
            var center = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

            for (int i = 0; i < cells; i++)
            {
                var it = shown[i];
                int col = i / rows, row = i % rows;
                float x = edge + col * (cellW + cellPad);
                var line = new RectangleF(x, row * rowH, cellW, rowH);
                if (more > 0 && i == cells - 1)
                {
                    using var moreBrush = new SolidBrush(Color.FromArgb(200, fore));
                    g.DrawString($"+{more} 台", textFont, moreBrush, line with { X = x + 2 * scale }, center);
                    continue;
                }
                var accent = Glyphs.Accent(it.Reading, fore);
                bool dim = it.Reading.State != ReadState.Ok;

                using var devBrush = new SolidBrush(dim ? Color.FromArgb(150, fore) : fore);
                g.DrawString(Glyphs.Device(it.Kind), glyphFont, devBrush, line with { Width = glyphW, Y = line.Y + scale }, center);

                using var accBrush = new SolidBrush(accent);
                // 自绘电池：外框用前景色，填充用强调色（充电绿 / 偏低黄 / 低电量红 / 正常前景色），充电时中间有闪电
                float bh = 9.5f * scale, bw = battW;
                var battRect = new RectangleF(x + glyphW + gap, line.Y + (rowH - bh) / 2f + scale * 0.5f, bw, bh);
                bool darkBar = fore.GetBrightness() > 0.5f;
                Glyphs.DrawBattery(g, battRect, it.Reading, fore, accent,
                    darkBar ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238));

                g.DrawString(Glyphs.Percent(it.Reading), textFont, accBrush,
                    line with { X = x + glyphW + gap + battW + gap, Width = textMax + 8 * scale }, center);
            }
        }
        return bmp;
    }

    private void EnsureLayoutHidden()
    {
        // 没有可显示的设备：把任务按钮区还原，隐藏自己
        if (IsWindow(_taskSw) && IsWindow(_rebar))
        {
            GetWindowRect(_rebar, out var rb);
            MoveWindow(_taskSw, _taskSwOrig.Left, 0, rb.Right - rb.Left - _taskSwOrig.Left, rb.Bottom - rb.Top, true);
        }
        if (IsHandleCreated) ShowWindow(Handle, SW_HIDE);
    }

    /// <summary>用 UpdateLayeredWindow 推送带透明度的位图（Win8+ 支持分层子窗口）。</summary>
    private void Push(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old); DeleteObject(hBmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Detach(); _guard.Dispose(); }
        base.Dispose(disposing);
    }

    // ---------- Win32 ----------

    const uint WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CLIPSIBLINGS = 0x04000000;
    const int GWL_STYLE = -16;
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
}
