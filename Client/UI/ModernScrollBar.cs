using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>
    /// Draws a vertical scrollbar with keyboard and pointer support.
    /// </summary>
    public sealed class ModernScrollBar : Control
    {
        private const int TrackPadding = 3;
        private const int MinimumThumbLength = 32;
        private int _maximum;
        private int _largeChange = 1;
        private int _value;
        private bool _hovered;
        private bool _dragging;
        private int _dragOffset;

        public ModernScrollBar()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);

            Width = 14;
            MinimumSize = new Size(10, 48);
            BackColor = Theme.Background;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = "Page scroll bar";
            AccessibleRole = AccessibleRole.ScrollBar;
        }

        public event EventHandler? ValueChanged;

        public int Maximum
        {
            get => _maximum;
            set
            {
                var next = Math.Max(0, value);
                if (_maximum == next) return;
                _maximum = next;
                Value = _value;
                Invalidate();
            }
        }

        public int LargeChange
        {
            get => _largeChange;
            set
            {
                var next = Math.Max(1, value);
                if (_largeChange == next) return;
                _largeChange = next;
                Invalidate();
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                var next = Math.Clamp(value, 0, Maximum);
                if (_value == next) return;
                _value = next;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }

        internal Rectangle ThumbBounds => CalculateThumbBounds(ClientSize, Maximum, LargeChange, Value);

        internal static Rectangle CalculateThumbBounds(Size size, int maximum, int largeChange, int value)
        {
            var trackTop = TrackPadding;
            var trackLength = Math.Max(1, size.Height - TrackPadding * 2);
            if (maximum <= 0)
                return new Rectangle(TrackPadding, trackTop, Math.Max(2, size.Width - TrackPadding * 2), trackLength);

            var totalExtent = Math.Max(1, maximum + Math.Max(1, largeChange));
            var thumbLength = Math.Clamp(
                (int)Math.Round(trackLength * (largeChange / (double)totalExtent)),
                Math.Min(MinimumThumbLength, trackLength),
                trackLength);
            var travel = Math.Max(0, trackLength - thumbLength);
            var clampedValue = Math.Clamp(value, 0, maximum);
            var thumbTop = trackTop + (int)Math.Round(travel * (clampedValue / (double)maximum));
            return new Rectangle(TrackPadding, thumbTop, Math.Max(2, size.Width - TrackPadding * 2), thumbLength);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            return key is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var smallChange = Math.Max(16, LargeChange / 10);
            switch (e.KeyCode)
            {
                case Keys.Up:
                    Value -= smallChange;
                    break;
                case Keys.Down:
                    Value += smallChange;
                    break;
                case Keys.PageUp:
                    Value -= LargeChange;
                    break;
                case Keys.PageDown:
                    Value += LargeChange;
                    break;
                case Keys.Home:
                    Value = 0;
                    break;
                case Keys.End:
                    Value = Maximum;
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Maximum <= 0) return;

            Focus();
            var thumb = ThumbBounds;
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.Y - thumb.Top;
                Capture = true;
            }
            else
            {
                Value += e.Y < thumb.Top ? -LargeChange : LargeChange;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging || Maximum <= 0) return;

            var thumb = ThumbBounds;
            var trackLength = Math.Max(1, Height - TrackPadding * 2);
            var travel = Math.Max(1, trackLength - thumb.Height);
            var requestedTop = Math.Clamp(e.Y - _dragOffset - TrackPadding, 0, travel);
            Value = (int)Math.Round(Maximum * (requestedTop / (double)travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
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
            if (!_dragging) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var trackBounds = new Rectangle(TrackPadding, TrackPadding, Math.Max(2, Width - TrackPadding * 2), Math.Max(1, Height - TrackPadding * 2));
            using (var trackPath = Theme.RoundedRect(trackBounds, trackBounds.Width / 2))
            using (var trackBrush = new SolidBrush(Theme.FieldBackground))
                e.Graphics.FillPath(trackBrush, trackPath);

            var thumb = ThumbBounds;
            var thumbColor = Maximum <= 0
                ? Theme.SoftBorder
                : Focused || _dragging
                    ? Theme.AccentBlue
                    : _hovered
                        ? Theme.MutedText
                        : Theme.FaintText;
            using var thumbPath = Theme.RoundedRect(thumb, thumb.Width / 2);
            using var thumbBrush = new SolidBrush(thumbColor);
            e.Graphics.FillPath(thumbBrush, thumbPath);
        }
    }
}
