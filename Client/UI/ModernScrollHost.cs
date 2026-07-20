using System;
using System.Drawing;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>
    /// Hosts clipped content with a custom dark vertical scrollbar.
    /// </summary>
    public sealed class ModernScrollHost : UserControl, IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly Panel _viewport;
        private readonly ModernScrollBar _scrollBar;
        private Control? _content;
        private bool _layoutInProgress;
        private int _lastContentWidth = -1;

        public ModernScrollHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.Background;
            TabStop = false;

            _scrollBar = new ModernScrollBar
            {
                Dock = DockStyle.Right,
                Width = 14,
                Margin = Padding.Empty
            };
            _viewport = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                TabStop = false
            };

            Controls.Add(_viewport);
            Controls.Add(_scrollBar);

            _scrollBar.ValueChanged += (_, _) => PositionContent();
            _viewport.Resize += (_, _) => LayoutContent();
        }

        public event EventHandler? ContentWidthChanged;

        public Padding ContentPadding { get; set; } = new(18, 14, 18, 18);
        public int MinimumContentWidth { get; set; } = 680;
        public int MaximumContentWidth { get; set; } = 760;
        public int ContentWidth => _lastContentWidth;

        public Control? Content
        {
            get => _content;
            set
            {
                if (ReferenceEquals(_content, value)) return;

                if (_content != null)
                {
                    _content.SizeChanged -= ContentOnSizeChanged;
                    _viewport.Controls.Remove(_content);
                }

                _content = value;
                _scrollBar.Value = 0;
                if (_content != null)
                {
                    _content.Margin = Padding.Empty;
                    _content.SizeChanged += ContentOnSizeChanged;
                    _viewport.Controls.Add(_content);
                    _content.BringToFront();
                }

                LayoutContent();
            }
        }

        public void RefreshScrollMetrics()
        {
            if (_content == null)
            {
                _scrollBar.Maximum = 0;
                return;
            }

            var extent = ScaleLogical(ContentPadding.Top) + _content.Height + ScaleLogical(ContentPadding.Bottom);
            _scrollBar.LargeChange = Math.Max(1, _viewport.ClientSize.Height);
            _scrollBar.Maximum = Math.Max(0, extent - _viewport.ClientSize.Height);
            PositionContent();
        }

        public void ScrollToTop() => _scrollBar.Value = 0;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || !Visible || !IsHandleCreated)
                return false;

            var target = Control.FromHandle(m.HWnd);
            if (target == null || !IsSelfOrDescendant(target) || PreservesOwnWheelBehavior(target))
                return false;

            var delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
            ScrollByWheelDelta(delta);
            return true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Application.AddMessageFilter(this);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Application.RemoveMessageFilter(this);
            base.OnHandleDestroyed(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollByWheelDelta(e.Delta);
        }

        private void LayoutContent()
        {
            if (_layoutInProgress || _content == null || _viewport.ClientSize.Width <= 0)
                return;

            _layoutInProgress = true;
            try
            {
                var horizontalPadding = ScaleLogical(ContentPadding.Left) + ScaleLogical(ContentPadding.Right);
                var availableWidth = Math.Max(1, _viewport.ClientSize.Width - horizontalPadding);
                var scaledMinimum = ScaleLogical(MinimumContentWidth);
                var scaledMaximum = ScaleLogical(MaximumContentWidth);
                var minimum = Math.Min(scaledMinimum, availableWidth);
                var width = Math.Clamp(availableWidth, minimum, Math.Max(minimum, scaledMaximum));

                if (_lastContentWidth != width)
                {
                    _lastContentWidth = width;
                    ContentWidthChanged?.Invoke(this, EventArgs.Empty);
                }

                // Recenter immediately while MainForm coalesces width changes.
                RefreshScrollMetrics();
            }
            finally
            {
                _layoutInProgress = false;
            }
        }

        private void PositionContent()
        {
            if (_content == null) return;
            var left = Math.Max(ScaleLogical(ContentPadding.Left), (_viewport.ClientSize.Width - _content.Width) / 2);
            var top = ScaleLogical(ContentPadding.Top) - _scrollBar.Value;
            if (_content.Left != left || _content.Top != top)
                _content.Location = new Point(left, top);
        }

        private void ContentOnSizeChanged(object? sender, EventArgs e) => RefreshScrollMetrics();

        private void ScrollByWheelDelta(int delta)
        {
            if (_scrollBar.Maximum <= 0 || delta == 0) return;
            var configuredLines = SystemInformation.MouseWheelScrollLines;
            var step = configuredLines < 0
                ? _scrollBar.LargeChange
                : Math.Max(ScaleLogical(32), configuredLines * ScaleLogical(16));
            var amount = (int)Math.Round(step * (delta / (double)SystemInformation.MouseWheelScrollDelta));
            if (amount == 0) amount = Math.Sign(delta);
            _scrollBar.Value -= amount;
        }

        private bool IsSelfOrDescendant(Control control)
        {
            for (Control? current = control; current != null; current = current.Parent)
            {
                if (ReferenceEquals(current, this)) return true;
            }

            return false;
        }

        private static bool PreservesOwnWheelBehavior(Control control) =>
            control is ComboBox or ListBox or TextBoxBase or NumericTextBox or ModernScrollBar;

        private int ScaleLogical(int value) => MainFormLayoutPolicy.ScaleLogical(value, DeviceDpi);
    }
}
