using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>Rounded, owner-drawn button with subtle hover and press states.</summary>
    public class ModernButton : Button
    {
        private bool _hovered;
        private bool _pressed;

        public ModernButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.RaisedBackground;
            ForeColor = Theme.Text;
            Font = Theme.ButtonFont;
            Cursor = Cursors.Hand;
            Height = 32;
            Padding = new Padding(10, 0, 10, 0);
        }

        public int CornerRadius { get; set; } = 7;
        public Color BorderColor { get; set; } = Theme.Border;
        public bool Emphasized { get; set; }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            pevent.Graphics.Clear(EffectiveParentBackColor());

            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            var baseFill = Enabled ? BackColor : Theme.DisabledFill;
            var fill = _pressed
                ? Theme.Blend(baseFill, Color.Black, 0.18f)
                : _hovered
                    ? Theme.Blend(baseFill, Color.White, 0.08f)
                    : baseFill;
            var border = Emphasized && Enabled ? Theme.Blend(baseFill, Color.White, 0.2f) : BorderColor;

            using var path = Theme.RoundedRect(rect, Math.Min(CornerRadius, Math.Min(rect.Width, rect.Height) / 2));
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(border, Focused ? 1.6f : 1f);
            pevent.Graphics.FillPath(brush, path);
            pevent.Graphics.DrawPath(pen, path);

            var textColor = Enabled ? ForeColor : Theme.DisabledText;
            TextRenderer.DrawText(
                pevent.Graphics,
                Text,
                Font,
                rect,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            base.OnMouseDown(mevent);
            if (mevent.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            base.OnMouseUp(mevent);
            _pressed = false;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        private Color EffectiveParentBackColor()
        {
            var color = Parent?.BackColor ?? Theme.Background;
            return color.A == byte.MaxValue ? color : Theme.CardBackground;
        }
    }
}
