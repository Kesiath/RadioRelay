using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>Compact status pill whose tint follows its ForeColor.</summary>
    public sealed class StatusBadge : Label
    {
        public StatusBadge()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
            AutoSize = false;
            TextAlign = ContentAlignment.MiddleCenter;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var parentColor = Parent?.BackColor ?? Theme.CardBackground;
            e.Graphics.Clear(parentColor.A == byte.MaxValue ? parentColor : Theme.CardBackground);

            var bounds = new Rectangle(0, 0, System.Math.Max(1, Width - 1), System.Math.Max(1, Height - 1));
            using var path = Theme.RoundedRect(bounds, Height / 2);
            using var fill = new SolidBrush(Theme.Blend(Theme.CardBackground, ForeColor, 0.14f));
            using var border = new Pen(Theme.Blend(Theme.Border, ForeColor, 0.35f));
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                bounds,
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
