using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>
    /// Draws a DPI-aware dark slider with standard range and value behavior.
    /// </summary>
    public sealed class ModernSlider : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;
        private bool _hovered;

        public ModernSlider()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);

            Height = 28;
            MinimumSize = new Size(60, 24);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.Slider;
        }

        public event EventHandler? ValueChanged;

        public int Minimum
        {
            get => _minimum;
            set
            {
                if (_minimum == value) return;
                _minimum = value;
                if (_maximum <= _minimum) _maximum = _minimum + 1;
                Value = Math.Clamp(_value, _minimum, _maximum);
                Invalidate();
            }
        }

        public int Maximum
        {
            get => _maximum;
            set
            {
                var coerced = Math.Max(_minimum + 1, value);
                if (_maximum == coerced) return;
                _maximum = coerced;
                Value = Math.Clamp(_value, _minimum, _maximum);
                Invalidate();
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                var coerced = Math.Clamp(value, _minimum, _maximum);
                if (_value == coerced) return;
                _value = coerced;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }

        public int SmallChange { get; set; } = 1;
        public int LargeChange { get; set; } = 5;
        public bool FocusOnPointerInteraction { get; set; } = true;
        private Color _accentColor = Theme.AccentGreen;

        public Color AccentColor
        {
            get => _accentColor;
            set
            {
                if (_accentColor == value) return;
                _accentColor = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = GetTrackBounds();
            var trackHeight = 5;
            var track = new Rectangle(bounds.Left, bounds.Top + (bounds.Height - trackHeight) / 2, bounds.Width, trackHeight);
            var thumbRadius = Focused || _dragging ? 7 : 6;
            var thumbX = ValueToX(_value, bounds);
            var fillWidth = Math.Max(trackHeight, thumbX - track.Left);
            var fill = new Rectangle(track.Left, track.Top, Math.Min(track.Width, fillWidth), track.Height);

            using (var trackPath = Theme.RoundedRect(track, trackHeight / 2))
            using (var trackBrush = new SolidBrush(Enabled ? Theme.SliderTrack : Theme.DisabledFill))
                e.Graphics.FillPath(trackBrush, trackPath);

            if (fill.Width > 0)
            {
                using var fillPath = Theme.RoundedRect(fill, trackHeight / 2);
                using var fillBrush = new SolidBrush(Enabled ? AccentColor : Theme.DisabledText);
                e.Graphics.FillPath(fillBrush, fillPath);
            }

            var thumb = new Rectangle(thumbX - thumbRadius, bounds.Top + bounds.Height / 2 - thumbRadius, thumbRadius * 2, thumbRadius * 2);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                e.Graphics.FillEllipse(shadowBrush, thumb.X + 1, thumb.Y + 2, thumb.Width, thumb.Height);

            using (var thumbBrush = new SolidBrush(Enabled ? (_hovered || _dragging ? Theme.Text : Theme.SliderThumb) : Theme.DisabledText))
                e.Graphics.FillEllipse(thumbBrush, thumb);

            using (var ringPen = new Pen(Enabled ? AccentColor : Theme.DisabledText, Focused ? 2f : 1f))
                e.Graphics.DrawEllipse(ringPen, thumb);
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
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            if (FocusOnPointerInteraction) Focus();
            Capture = true;
            _dragging = true;
            SetValueFromX(e.X);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetValueFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Capture = false;
            _dragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;

            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Down:
                    Value -= Math.Max(1, SmallChange);
                    e.Handled = true;
                    break;
                case Keys.Right:
                case Keys.Up:
                    Value += Math.Max(1, SmallChange);
                    e.Handled = true;
                    break;
                case Keys.PageDown:
                    Value -= Math.Max(1, LargeChange);
                    e.Handled = true;
                    break;
                case Keys.PageUp:
                    Value += Math.Max(1, LargeChange);
                    e.Handled = true;
                    break;
                case Keys.Home:
                    Value = Minimum;
                    e.Handled = true;
                    break;
                case Keys.End:
                    Value = Maximum;
                    e.Handled = true;
                    break;
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        private Rectangle GetTrackBounds()
        {
            const int horizontalPadding = 8;
            return new Rectangle(horizontalPadding, 0, Math.Max(1, Width - horizontalPadding * 2 - 1), Height);
        }

        private int ValueToX(int value, Rectangle bounds)
        {
            var ratio = (value - _minimum) / (double)Math.Max(1, _maximum - _minimum);
            return bounds.Left + (int)Math.Round(ratio * bounds.Width);
        }

        private void SetValueFromX(int x)
        {
            var bounds = GetTrackBounds();
            var ratio = Math.Clamp((x - bounds.Left) / (double)Math.Max(1, bounds.Width), 0d, 1d);
            Value = _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
        }
    }
}
