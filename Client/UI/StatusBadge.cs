using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>Compact status pill whose tint follows its ForeColor.</summary>
    public sealed class StatusBadge : Label
    {
        private bool _flatRightEdge;

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

        public bool FlatRightEdge
        {
            get => _flatRightEdge;
            set
            {
                if (_flatRightEdge == value) return;
                _flatRightEdge = value;
                Invalidate();
            }
        }

        internal Color EffectiveBorderColor =>
            FlatRightEdge && BackColor.A > 0
                ? BackColor
                : Theme.Blend(Theme.Border, ForeColor, 0.35f);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var parentColor = Parent?.BackColor ?? Theme.CardBackground;
            e.Graphics.Clear(parentColor.A == byte.MaxValue ? parentColor : Theme.CardBackground);

            var bounds = new Rectangle(0, 0, System.Math.Max(1, Width - 1), System.Math.Max(1, Height - 1));
            using var path = CreateBadgePath(bounds);
            using var fill = new SolidBrush(Theme.Blend(Theme.CardBackground, ForeColor, 0.14f));
            using var border = new Pen(EffectiveBorderColor);
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

        private GraphicsPath CreateBadgePath(Rectangle bounds)
        {
            if (!FlatRightEdge)
                return Theme.RoundedRect(bounds, Height / 2);

            float left = bounds.Left;
            float top = bounds.Top;
            float right = System.Math.Max(left, Width - 1f);
            float bottom = System.Math.Max(top, Height - 1f);
            float radius = System.Math.Max(0f, System.Math.Min(Height / 2f, (bottom - top) / 2f));
            float diameter = radius * 2f;

            var path = new GraphicsPath();
            path.StartFigure();
            path.AddLine(left + radius, top, right, top);
            path.AddLine(right, top, right, bottom);
            path.AddLine(right, bottom, left + radius, bottom);
            if (diameter > 0f)
            {
                path.AddArc(left, bottom - diameter, diameter, diameter, 90f, 90f);
                path.AddLine(left, bottom - radius, left, top + radius);
                path.AddArc(left, top, diameter, diameter, 180f, 90f);
            }
            path.CloseFigure();
            return path;
        }
    }
}
