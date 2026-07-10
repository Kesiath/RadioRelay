using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>Owner-drawn title bar used by the frameless main window.</summary>
    public sealed class ModernTitleBar : Control
    {
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONUP = 0x00A5;
        private const int HTCAPTION = 2;
        private readonly CaptionButton _minimizeButton;
        private readonly CaptionButton _maximizeButton;
        private readonly CaptionButton _closeButton;
        private string _title = "RadioRelay";
        private string _subtitle = string.Empty;
        private bool _active = true;
        private bool _maximizeAvailable = true;

        public ModernTitleBar()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
            true);

            Dock = DockStyle.Top;
            BackColor = Theme.Background;
            TabStop = false;

            _minimizeButton = new CaptionButton(CaptionButtonKind.Minimize)
            {
                AccessibleName = "Minimize RadioRelay"
            };
            _maximizeButton = new CaptionButton(CaptionButtonKind.Maximize)
            {
                AccessibleName = "Maximize RadioRelay"
            };
            _closeButton = new CaptionButton(CaptionButtonKind.Close)
            {
                AccessibleName = "Close RadioRelay"
            };

            _minimizeButton.Click += (_, _) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
            _maximizeButton.Click += (_, _) => MaximizeRestoreRequested?.Invoke(this, EventArgs.Empty);
            _closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

            Controls.Add(_minimizeButton);
            Controls.Add(_maximizeButton);
            Controls.Add(_closeButton);
            Height = 44;
        }

        public event EventHandler? MinimizeRequested;
        public event EventHandler? MaximizeRestoreRequested;
        public event EventHandler? CloseRequested;

        public string Title
        {
            get => _title;
            set
            {
                value ??= string.Empty;
                if (_title == value) return;
                _title = value;
                Invalidate();
            }
        }

        public string Subtitle
        {
            get => _subtitle;
            set
            {
                value ??= string.Empty;
                if (_subtitle == value) return;
                _subtitle = value;
                Invalidate();
            }
        }

        public bool MaximizeAvailable
        {
            get => _maximizeAvailable;
            set
            {
                if (_maximizeAvailable == value) return;
                _maximizeAvailable = value;
                _maximizeButton.Visible = value;
                PerformLayout();
                Invalidate();
            }
        }

        public void SetWindowState(bool maximized)
        {
            _maximizeButton.Kind = maximized ? CaptionButtonKind.Restore : CaptionButtonKind.Maximize;
            _maximizeButton.AccessibleName = maximized ? "Restore RadioRelay" : "Maximize RadioRelay";
        }

        public void SetWindowActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            Invalidate();
        }

        /// <summary>Returns true when a point is over a control that must receive client input.</summary>
        public bool IsInteractiveAt(Point clientPoint) =>
            _minimizeButton.Bounds.Contains(clientPoint) ||
            (_maximizeAvailable && _maximizeButton.Bounds.Contains(clientPoint)) ||
            _closeButton.Bounds.Contains(clientPoint);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            const int buttonWidth = 46;
            var buttonHeight = Math.Max(1, ClientSize.Height - 2);
            _closeButton.SetBounds(Math.Max(0, ClientSize.Width - buttonWidth), 1, buttonWidth, buttonHeight);
            if (_maximizeAvailable)
            {
                _maximizeButton.SetBounds(Math.Max(0, _closeButton.Left - buttonWidth), 1, buttonWidth, buttonHeight);
                _minimizeButton.SetBounds(Math.Max(0, _maximizeButton.Left - buttonWidth), 1, buttonWidth, buttonHeight);
            }
            else
            {
                _minimizeButton.SetBounds(Math.Max(0, _closeButton.Left - buttonWidth), 1, buttonWidth, buttonHeight);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var dotColor = _active ? Theme.AccentGreen : Theme.FaintText;
            using (var dotBrush = new SolidBrush(dotColor))
                e.Graphics.FillEllipse(dotBrush, 16, (Height - 8) / 2f, 8, 8);

            var titleColor = _active ? Theme.Text : Theme.MutedText;
            var titleBounds = new Rectangle(32, 0, Math.Max(0, _minimizeButton.Left - 44), Height);
            TextRenderer.DrawText(
                e.Graphics,
                _title,
                Theme.TitleFont,
                titleBounds,
                titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrWhiteSpace(_subtitle))
            {
                var titleWidth = TextRenderer.MeasureText(_title, Theme.TitleFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                var subtitleBounds = new Rectangle(40 + titleWidth, 0, Math.Max(0, _minimizeButton.Left - titleWidth - 52), Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    _subtitle,
                    Theme.BodyFont,
                    subtitleBounds,
                    _active ? Theme.MutedText : Theme.FaintText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            using var divider = new Pen(Theme.SoftBorder);
            e.Graphics.DrawLine(divider, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var form = FindForm();
            if (form == null || IsInteractiveAt(e.Location)) return;

            var screenPoint = PointToScreen(e.Location);
            var packedPoint = PackScreenPoint(screenPoint);
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                _ = SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, packedPoint);
            }
            else if (e.Button == MouseButtons.Right)
            {
                _ = SendMessage(form.Handle, WM_NCRBUTTONUP, (IntPtr)HTCAPTION, packedPoint);
            }
        }

        private static IntPtr PackScreenPoint(Point point)
        {
            var packed = (point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16);
            return (IntPtr)packed;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    }

    internal enum CaptionButtonKind
    {
        Minimize,
        Maximize,
        Restore,
        Close
    }

    internal sealed class CaptionButton : Control
    {
        private bool _hovered;
        private bool _pressed;
        private CaptionButtonKind _kind;

        public CaptionButton(CaptionButtonKind kind)
        {
            _kind = kind;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.Selectable,
                true);

            BackColor = Theme.Background;
            Cursor = Cursors.Hand;
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public CaptionButtonKind Kind
        {
            get => _kind;
            set
            {
                if (_kind == value) return;
                _kind = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var baseFill = Kind == CaptionButtonKind.Close && _hovered
                ? Theme.AccentRed
                : _hovered
                    ? Theme.RaisedBackground
                    : Theme.Background;
            var fill = _pressed ? Theme.Blend(baseFill, Color.Black, 0.22f) : baseFill;
            e.Graphics.Clear(fill);

            var glyphColor = Kind == CaptionButtonKind.Close && _hovered ? Color.White : Theme.Text;
            using var pen = new Pen(glyphColor, 1.35f)
            {
                StartCap = LineCap.Square,
                EndCap = LineCap.Square
            };

            var cx = Width / 2f;
            var cy = Height / 2f;
            switch (Kind)
            {
                case CaptionButtonKind.Minimize:
                    e.Graphics.DrawLine(pen, cx - 5, cy + 3, cx + 5, cy + 3);
                    break;
                case CaptionButtonKind.Maximize:
                    e.Graphics.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
                    break;
                case CaptionButtonKind.Restore:
                    e.Graphics.DrawRectangle(pen, cx - 3, cy - 5, 9, 9);
                    e.Graphics.DrawRectangle(pen, cx - 6, cy - 2, 9, 9);
                    break;
                case CaptionButtonKind.Close:
                    e.Graphics.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                    e.Graphics.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                    break;
            }
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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            Invalidate();
        }
    }
}
