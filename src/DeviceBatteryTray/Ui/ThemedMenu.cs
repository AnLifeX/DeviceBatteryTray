using System.Drawing;
using System.Drawing.Drawing2D;

namespace DeviceBatteryTray.Ui;

/// <summary>下拉选项菜单：跟随主题配色，选中项左侧显示强调色竖条（Win11 下拉框样式）。</summary>
public static class ThemedMenu
{
    public sealed record Option(string Text, bool Selected, Action Pick);

    public static void Show(Control owner, Rectangle anchor, IEnumerable<Option> options, float s)
    {
        var t = Theme.Current;
        var menu = new ContextMenuStrip
        {
            Renderer = new Renderer(t, s),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            BackColor = t.Surface,
            ForeColor = t.Text,
            Font = new Font(Draw.UiFont, 13 * s, GraphicsUnit.Pixel),
            Padding = new Padding((int)(4 * s)),
            DropShadowEnabled = true,
        };
        foreach (var o in options)
        {
            var item = new ToolStripMenuItem(o.Text)
            {
                Checked = o.Selected,
                AutoSize = false,
                Size = new Size(Math.Max(anchor.Width, (int)(140 * s)), (int)(34 * s)),
                Padding = new Padding((int)(8 * s), 0, 0, 0),
                ForeColor = t.Text,
            };
            item.Click += (_, _) => o.Pick();
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => owner.BeginInvoke(menu.Dispose);
        menu.Show(owner, new Point(anchor.X, anchor.Bottom + (int)(4 * s)));
    }

    sealed class Renderer : ToolStripRenderer
    {
        readonly Theme _t;
        readonly float _s;
        public Renderer(Theme t, float s) { _t = t; _s = s; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(_t.Surface);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(_t.Border);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(2 * _s, 1 * _s, e.Item.Width - 4 * _s, e.Item.Height - 2 * _s);
            var item = (ToolStripMenuItem)e.Item;
            if (item.Selected || item.Checked) Draw.Fill(g, item.Selected ? _t.Hover : Color.FromArgb(_t.Dark ? 10 : 6, _t.Dark ? Color.White : Color.Black), r, 4 * _s);
            if (item.Checked)
                Draw.Fill(g, _t.Accent, new RectangleF(r.X, r.Y + r.Height / 2 - 8 * _s, 3 * _s, 16 * _s), 1.5f * _s);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = _t.Text;
            e.TextRectangle = e.TextRectangle with { X = (int)(14 * _s) };
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { } // 用竖条代替勾
    }
}
