using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    internal readonly record struct MembershipTooltipLayout(
        int Width,
        int Height,
        int Columns,
        int Rows,
        int ColumnWidth)
    {
        internal const int OuterPadding = 12;
        internal const int HeaderHeight = 30;
        internal const int LineHeight = 20;
        internal const int ColumnGap = 18;
        internal const int PreferredRows = 20;

        internal static MembershipTooltipLayout Calculate(int itemCount, int widestText, int maxWidth)
        {
            itemCount = Math.Max(1, itemCount);
            maxWidth = Math.Max(180, maxWidth);
            int columnWidth = Math.Clamp(widestText + 8, 120, 220);
            int maximumColumns = Math.Max(1,
                (maxWidth - OuterPadding * 2 + ColumnGap) / (columnWidth + ColumnGap));
            int preferredColumns = Math.Max(1, (int)Math.Ceiling(itemCount / (double)PreferredRows));
            int columns = Math.Min(preferredColumns, maximumColumns);
            int rows = (int)Math.Ceiling(itemCount / (double)columns);
            int width = OuterPadding * 2 + columns * columnWidth + (columns - 1) * ColumnGap;
            int height = OuterPadding * 2 + HeaderHeight + rows * LineHeight;
            return new MembershipTooltipLayout(width, height, columns, rows, columnWidth);
        }

        internal Rectangle ItemBounds(int index)
        {
            int column = index / Rows;
            int row = index % Rows;
            return new Rectangle(
                OuterPadding + column * (ColumnWidth + ColumnGap),
                OuterPadding + HeaderHeight + row * LineHeight,
                ColumnWidth,
                LineHeight);
        }
    }

    /// <summary>
    /// Hover membership popup implemented as a real borderless RadioRelay
    /// window rather than a native Windows tooltip. Its window region is
    /// rounded, so no rectangular native background can leak around corners.
    /// </summary>
    internal sealed class MembershipToolTip : IDisposable
    {
        private sealed record Content(string Title, string[] Names);

        private readonly Dictionary<Control, Content> _content = new();
        private readonly MembershipPopupForm _popup = new();
        private readonly System.Windows.Forms.Timer _showTimer = new() { Interval = 250 };
        private Control? _pendingControl;

        public MembershipToolTip()
        {
            _showTimer.Tick += (_, _) =>
            {
                _showTimer.Stop();
                if (_pendingControl != null && !_pendingControl.IsDisposed)
                    ShowFor(_pendingControl);
            };
        }

        public void SetMembership(Control control, string title, IEnumerable<string>? names)
        {
            var sorted = (names ?? Array.Empty<string>())
                .Select(name => string.IsNullOrWhiteSpace(name) ? "(no callsign)" : name.Trim())
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (sorted.Length == 0) sorted = new[] { "No connected clients" };
            _content[control] = new Content(title, sorted);

            if (control.Tag is MembershipHoverMarker) return;
            // Tag is already used by some WinForms controls, so retain it in
            // the marker and restore it when this helper is disposed.
            control.Tag = new MembershipHoverMarker(control.Tag);
            control.MouseEnter += OnMouseEnter;
            control.MouseLeave += OnMouseLeave;
            control.Disposed += OnControlDisposed;
        }

        private void OnMouseEnter(object? sender, EventArgs e)
        {
            if (sender is not Control control) return;
            _pendingControl = control;
            _showTimer.Stop();
            _showTimer.Start();
        }

        private void OnMouseLeave(object? sender, EventArgs e)
        {
            if (ReferenceEquals(_pendingControl, sender)) _pendingControl = null;
            _showTimer.Stop();
            _popup.Hide();
        }

        private void OnControlDisposed(object? sender, EventArgs e)
        {
            if (sender is Control control) _content.Remove(control);
        }

        private void ShowFor(Control control)
        {
            if (!_content.TryGetValue(control, out var content)) return;
            var workingArea = Screen.FromControl(control).WorkingArea;
            _popup.SetContent(content.Title, content.Names, Math.Min(760, workingArea.Width - 32));

            var anchor = control.PointToScreen(new Point(0, control.Height + 6));
            int left = Math.Clamp(anchor.X, workingArea.Left + 8, Math.Max(workingArea.Left + 8, workingArea.Right - _popup.Width - 8));
            int top = anchor.Y;
            if (top + _popup.Height > workingArea.Bottom - 8)
                top = control.PointToScreen(Point.Empty).Y - _popup.Height - 6;
            top = Math.Clamp(top, workingArea.Top + 8, Math.Max(workingArea.Top + 8, workingArea.Bottom - _popup.Height - 8));

            _popup.Location = new Point(left, top);
            _popup.Show();
            _popup.BringToFront();
        }

        public void Dispose()
        {
            _showTimer.Stop();
            _showTimer.Dispose();
            foreach (var control in _content.Keys.ToArray())
            {
                if (control.IsDisposed) continue;
                control.MouseEnter -= OnMouseEnter;
                control.MouseLeave -= OnMouseLeave;
                control.Disposed -= OnControlDisposed;
                if (control.Tag is MembershipHoverMarker marker) control.Tag = marker.PreviousTag;
            }
            _content.Clear();
            _popup.Close();
            _popup.Dispose();
        }

        private sealed record MembershipHoverMarker(object? PreviousTag);

        private sealed class MembershipPopupForm : Form
        {
            private const int WS_EX_TOOLWINDOW = 0x80;
            private const int WS_EX_NOACTIVATE = 0x08000000;

            private string _title = "Members";
            private string[] _names = Array.Empty<string>();
            private MembershipTooltipLayout _layout;

            public MembershipPopupForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = Theme.CardBackground;
                DoubleBuffered = true;
                AutoScaleMode = AutoScaleMode.Dpi;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return parameters;
                }
            }

            public void SetContent(string title, string[] names, int maxWidth)
            {
                _title = title;
                _names = names;
                int widestName = names.Max(name =>
                    TextRenderer.MeasureText(name, Theme.BodyFont, Size.Empty, TextFormatFlags.NoPadding).Width);
                int titleWidth = TextRenderer.MeasureText(
                    title, Theme.ButtonFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                _layout = MembershipTooltipLayout.Calculate(names.Length, Math.Max(widestName, titleWidth), maxWidth);
                ClientSize = new Size(_layout.Width, _layout.Height);
                UpdateRoundedRegion();
                Invalidate();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                UpdateRoundedRegion();
            }

            private void UpdateRoundedRegion()
            {
                if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
                using var path = Theme.RoundedRect(ClientRectangle, 9);
                var oldRegion = Region;
                Region = new Region(path);
                oldRegion?.Dispose();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Theme.CardBackground);
                var cardBounds = Rectangle.Inflate(ClientRectangle, -1, -1);
                using (var path = Theme.RoundedRect(cardBounds, 9))
                using (var border = new Pen(Theme.Border))
                    e.Graphics.DrawPath(border, path);

                var titleBounds = new Rectangle(
                    MembershipTooltipLayout.OuterPadding,
                    MembershipTooltipLayout.OuterPadding,
                    Math.Max(1, ClientSize.Width - MembershipTooltipLayout.OuterPadding * 2),
                    MembershipTooltipLayout.HeaderHeight - 5);
                TextRenderer.DrawText(e.Graphics, _title, Theme.ButtonFont, titleBounds, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                int separatorY = MembershipTooltipLayout.OuterPadding + MembershipTooltipLayout.HeaderHeight - 4;
                using (var separator = new Pen(Theme.SoftBorder))
                    e.Graphics.DrawLine(separator, MembershipTooltipLayout.OuterPadding, separatorY,
                        ClientSize.Width - MembershipTooltipLayout.OuterPadding, separatorY);

                for (int i = 0; i < _names.Length; i++)
                {
                    TextRenderer.DrawText(e.Graphics, _names[i], Theme.BodyFont, _layout.ItemBounds(i), Theme.MutedText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                        TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
        }
    }
}
