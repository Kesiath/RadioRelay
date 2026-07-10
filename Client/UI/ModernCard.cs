using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>
    /// Rounded TableLayoutPanel used as the structural card throughout the
    /// main window. It remains a normal layout container, so existing controls
    /// and event wiring are unchanged.
    /// </summary>
    public class ModernCard : TableLayoutPanel
    {
        public ModernCard()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);

            BackColor = Color.Transparent;
            FillColor = Theme.CardBackground;
            BorderColor = Theme.Border;
            CornerRadius = 12;
            Margin = Padding.Empty;
        }

        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public Color AccentColor { get; set; } = Color.Transparent;
        public int AccentWidth { get; set; }
        public int CornerRadius { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(EffectiveParentBackColor());

            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = Theme.RoundedRect(rect, Math.Min(CornerRadius, Math.Min(rect.Width, rect.Height) / 2));
            using var fill = new SolidBrush(FillColor);
            e.Graphics.FillPath(fill, path);

            if (AccentWidth > 0 && AccentColor != Color.Transparent)
            {
                var accentRect = new Rectangle(1, CornerRadius, AccentWidth, Math.Max(0, Height - CornerRadius * 2));
                using var accentBrush = new SolidBrush(AccentColor);
                e.Graphics.FillRectangle(accentBrush, accentRect);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = Theme.RoundedRect(rect, Math.Min(CornerRadius, Math.Min(rect.Width, rect.Height) / 2));
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawPath(pen, path);
        }

        private Color EffectiveParentBackColor()
        {
            var color = Parent?.BackColor ?? Theme.Background;
            return color.A == byte.MaxValue ? color : Theme.Background;
        }
    }
}

namespace RadioRelay.Client.UI
{
    /// <summary>Rounded card variant for free-form docked content.</summary>
    public class ModernPanel : Panel
    {
        public ModernPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
            FillColor = Theme.CardBackground;
            BorderColor = Theme.Border;
            CornerRadius = 12;
        }

        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public int CornerRadius { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(EffectiveParentBackColor());
            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = Theme.RoundedRect(rect, Math.Min(CornerRadius, Math.Min(rect.Width, rect.Height) / 2));
            using var fill = new SolidBrush(FillColor);
            e.Graphics.FillPath(fill, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = Theme.RoundedRect(rect, Math.Min(CornerRadius, Math.Min(rect.Width, rect.Height) / 2));
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawPath(pen, path);
        }

        private Color EffectiveParentBackColor()
        {
            var color = Parent?.BackColor ?? Theme.Background;
            return color.A == byte.MaxValue ? color : Theme.Background;
        }
    }
}
