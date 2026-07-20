using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RadioRelay.Client;
using RadioRelay.Client.Radio;

namespace RadioRelay.Client.UI
{
    /// <summary>
    /// Displays draggable, per-radio TX and RX activity chips in a click-through overlay.
    /// </summary>
    public class TransmissionOverlayForm : Form
    {
        private class ChipState
        {
            public string? TxWho;
            public readonly Dictionary<string, string> RxWhos = new();
            public int UserCount;
            public bool HasContent => TxWho != null || RxWhos.Count > 0;
        }

        /// <summary>
        /// Stores one activity header and its wrapped callsign lines.
        /// </summary>
        private readonly record struct ChipBlock(string Header, IReadOnlyList<string> WhoLines, Color WhoColor);

        private readonly List<RadioChannel> _channels;
        private readonly Dictionary<RadioChannel, ChipState> _chips = new();
        private readonly Dictionary<RadioChannel, long> _lastTxLifecycleByChannel = new();
        private readonly Dictionary<RadioChannel, Dictionary<string, long>> _lastRxLifecycleByChannel = new();
        private readonly object _lock = new();

        private const int MinChipWidth = 260;
        private const int MaxChipWidth = 520; // Truncate wider text with an ellipsis.
        private const int TextPaddingLeft = 12;
        private const int TextPaddingRight = 14;
        private const int RowHeight = 24;
        private const int ChipPadding = 8;
        private const int ScreenMargin = 16; // Default distance from screen edges.
        private const int DefaultGap = 8;

        // Reserve enough default height for simultaneous TX and RX blocks.
        private const int DefaultSlotHeight = ChipPadding * 2 + 4 * RowHeight;

        private bool _editMode;
        private RadioChannel? _draggingChannel;
        private Point _dragOffset;

        /// <summary>
        /// Raised after a chip is moved so its position can be persisted.
        /// </summary>
        public event Action? LayoutChanged;

        public TransmissionOverlayForm(List<RadioChannel> channels)
        {
            _channels = channels;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(1, 2, 3); // Chroma key excluded from drawn content.
            TransparencyKey = BackColor;
            Opacity = 0.80;
            DoubleBuffered = true;

            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Bounds = bounds;
            Visible = true; // Painting controls individual chip visibility.
        }

        // Create a layered, click-through tool window excluded from Alt+Tab.
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;

        /// <summary>
        /// Enables chip dragging while keeping non-chip areas click-through.
        /// </summary>
        public void SetEditMode(bool enabled)
        {
            _editMode = enabled;
            SafeInvoke(() =>
            {
                if (IsHandleCreated)
                {
                    int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
                    exStyle = enabled ? (exStyle & ~WS_EX_TRANSPARENT) : (exStyle | WS_EX_TRANSPARENT);
                    SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
                }
                Invalidate();
            });
        }

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTTRANSPARENT = -1;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && _editMode)
            {
                // lParam packs screen coordinates as two 16-bit signed shorts.
                int screenX = unchecked((short)(m.LParam.ToInt64() & 0xFFFF));
                int screenY = unchecked((short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                var clientPoint = PointToClient(new Point(screenX, screenY));

                m.Result = (IntPtr)(IsOverAnyChip(clientPoint) ? HTCLIENT : HTTRANSPARENT);
                return;
            }

            base.WndProc(ref m);
        }

        private bool IsOverAnyChip(Point clientPoint)
        {
            for (int i = 0; i < _channels.Count; i++)
            {
                if (GetChipBounds(_channels[i], i).Contains(clientPoint)) return true;
            }
            return false;
        }

        /// <summary>
        /// Call when a transmission (local PTT or a received
        /// packet's start) begins. Safe to call from any thread.
        /// </summary>
        public void ShowTransmission(
            RadioChannel channel,
            bool isLocalTransmit,
            string remoteCallsign,
            string localCallsign,
            string remoteClientId = "",
            long? lifecycleSequence = null)
        {
            if (channel.Volume <= 0f) return;

            string who = isLocalTransmit
                ? (string.IsNullOrWhiteSpace(localCallsign) ? "You" : localCallsign)
                : (string.IsNullOrWhiteSpace(remoteCallsign) ? "Unknown" : remoteCallsign);

            lock (_lock)
            {
                if (!_chips.TryGetValue(channel, out var chip)) { chip = new ChipState(); _chips[channel] = chip; }
                if (isLocalTransmit)
                {
                    if (!AcceptTxLifecycle(channel, lifecycleSequence)) return;
                    chip.TxWho = who;
                }
                else
                {
                    var key = string.IsNullOrWhiteSpace(remoteClientId) ? who : remoteClientId;
                    if (!AcceptRxLifecycle(channel, key, lifecycleSequence)) return;
                    chip.RxWhos[key] = who;
                }
            }
            SafeInvoke(Invalidate);
        }

        /// <summary>
        /// Removes transmission content for an off radio while preserving its
        /// independently-updated presence count.
        /// </summary>
        public void SuppressChannel(RadioChannel channel)
        {
            lock (_lock)
            {
                if (!_chips.TryGetValue(channel, out var chip)) return;
                chip.TxWho = null;
                chip.RxWhos.Clear();
                if (chip.UserCount == 0) _chips.Remove(channel);
            }
            SafeInvoke(Invalidate);
        }

        /// <summary>
        /// Call when a transmission ends. Safe to call from any thread.
        /// </summary>
        public void HideTransmission(
            RadioChannel channel,
            bool isLocalTransmit,
            string remoteClientId = "",
            long? lifecycleSequence = null)
        {
            lock (_lock)
            {
                if (isLocalTransmit)
                {
                    if (!AcceptTxLifecycle(channel, lifecycleSequence)) return;
                }
                else if (!string.IsNullOrWhiteSpace(remoteClientId))
                {
                    if (!AcceptRxLifecycle(channel, remoteClientId, lifecycleSequence)) return;
                }

                if (_chips.TryGetValue(channel, out var chip))
                {
                    if (isLocalTransmit)
                    {
                        chip.TxWho = null;
                    }
                    else if (!string.IsNullOrWhiteSpace(remoteClientId))
                    {
                        chip.RxWhos.Remove(remoteClientId);
                    }
                    else
                    {
                        chip.RxWhos.Clear();
                    }
                    if (!chip.HasContent && chip.UserCount == 0) _chips.Remove(channel);
                }
            }
            SafeInvoke(Invalidate);
        }

        private bool AcceptTxLifecycle(RadioChannel channel, long? lifecycleSequence)
        {
            if (lifecycleSequence == null) return true;
            if (_lastTxLifecycleByChannel.TryGetValue(channel, out var lastSeen) && lifecycleSequence.Value <= lastSeen)
                return false;

            _lastTxLifecycleByChannel[channel] = lifecycleSequence.Value;
            return true;
        }

        private bool AcceptRxLifecycle(RadioChannel channel, string remoteKey, long? lifecycleSequence)
        {
            if (lifecycleSequence == null) return true;
            if (!_lastRxLifecycleByChannel.TryGetValue(channel, out var lastByRemote))
            {
                lastByRemote = new Dictionary<string, long>();
                _lastRxLifecycleByChannel[channel] = lastByRemote;
            }

            if (lastByRemote.TryGetValue(remoteKey, out var lastSeen) && lifecycleSequence.Value <= lastSeen)
                return false;

            lastByRemote[remoteKey] = lifecycleSequence.Value;
            return true;
        }

        /// <summary>
        /// Updates the number of users currently subscribed to this
        /// radio's frequency/key group. Safe to call from any thread.
        /// </summary>
        public void SetUserCount(RadioChannel channel, int userCount)
        {
            lock (_lock)
            {
                if (!_chips.TryGetValue(channel, out var chip)) { chip = new ChipState(); _chips[channel] = chip; }
                chip.UserCount = Math.Max(0, userCount);
                if (!chip.HasContent && chip.UserCount == 0) _chips.Remove(channel);
            }
            SafeInvoke(Invalidate);
        }

        private void SafeInvoke(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Builds activity blocks shared by painting, sizing, and hit testing.
        /// </summary>
        private List<ChipBlock> GetBlocksForChannel(RadioChannel ch)
        {
            var blocks = new List<ChipBlock>();
            if (ch.Volume <= 0f) return blocks;

            ChipState? chip;
            lock (_lock) { _chips.TryGetValue(ch, out chip); }

            var userCountSuffix = chip != null || _editMode ? $"  •  {PresenceDisplay.FormatCount(chip?.UserCount ?? 0)}" : "";

            if (chip?.TxWho != null)
                blocks.Add(new ChipBlock($"TX ▶  {ch.DisplayName}  {ch.Frequency:0.000} MHz{userCountSuffix}", new[] { chip.TxWho }, Theme.TxUsername));
            if (chip?.RxWhos.Count > 0)
                blocks.Add(new ChipBlock($"RX ◀  {ch.DisplayName}  {ch.Frequency:0.000} MHz{userCountSuffix}", PackWhoLines(chip.RxWhos.Values), Theme.RxUsername));

            if (blocks.Count == 0 && _editMode)
                blocks.Add(new ChipBlock($"TX ▶  {ch.DisplayName}  {ch.Frequency:0.000} MHz{userCountSuffix}", new[] { "Preview" }, Theme.TxUsername));

            return blocks;
        }

        internal IReadOnlyList<string> GetHeadersForTest(RadioChannel channel) =>
            GetBlocksForChannel(channel).ConvertAll(block => block.Header);

        internal IReadOnlyList<string> GetWhoLinesForTest(RadioChannel channel) =>
            GetBlocksForChannel(channel)
                .SelectMany(block => block.WhoLines)
                .ToArray();

        private static IReadOnlyList<string> PackWhoLines(IEnumerable<string> names)
        {
            var rows = new List<string>();
            string? currentRow = null;
            var maxContentWidth = MinChipWidth - TextPaddingLeft - TextPaddingRight;

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(currentRow))
                {
                    currentRow = name;
                    continue;
                }

                var candidate = $"{currentRow}, {name}";
                if (TextRenderer.MeasureText(candidate, Theme.TitleFont).Width <= maxContentWidth)
                {
                    currentRow = candidate;
                }
                else
                {
                    rows.Add(currentRow);
                    currentRow = name;
                }
            }

            if (!string.IsNullOrWhiteSpace(currentRow)) rows.Add(currentRow);
            return rows;
        }

        private Point DefaultPositionFor(int index, int chipWidth)
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int x = area.Right - chipWidth - ScreenMargin;
            int y = area.Bottom - ScreenMargin - (index + 1) * (DefaultSlotHeight + DefaultGap);
            return new Point(x, y);
        }

        /// <summary>
        /// Computes a channel chip rectangle using the shared visible-chip width.
        /// </summary>
        private Rectangle GetChipBounds(RadioChannel channel, int index)
        {
            var blocks = GetBlocksForChannel(channel);
            if (blocks.Count == 0) return Rectangle.Empty;

            int lineCount = blocks.Sum(block => 1 + block.WhoLines.Count);
            int width = GetConsistentChipWidth();
            int height = ChipPadding * 2 + lineCount * RowHeight;

            var pos = channel.HudPosition ?? DefaultPositionFor(index, width);
            return new Rectangle(pos.X, pos.Y, width, height);
        }

        private int GetConsistentChipWidth()
        {
            var widest = MinChipWidth;
            foreach (var ch in _channels)
            {
                var blocks = GetBlocksForChannel(ch);
                if (blocks.Count == 0) continue;

                widest = Math.Max(widest, MeasureChipWidth(blocks));
            }

            return widest;
        }

        private static int MeasureChipWidth(IReadOnlyList<ChipBlock> blocks)
        {
            int maxTextWidth = 0;
            foreach (var block in blocks)
            {
                maxTextWidth = Math.Max(maxTextWidth, TextRenderer.MeasureText(HeaderForSizing(block.Header), Theme.TitleFont).Width);
                foreach (var whoLine in block.WhoLines)
                {
                    maxTextWidth = Math.Max(maxTextWidth, TextRenderer.MeasureText(whoLine, Theme.TitleFont).Width);
                }
            }

            return Math.Clamp(maxTextWidth + TextPaddingLeft + TextPaddingRight, MinChipWidth, MaxChipWidth);
        }

        private static string HeaderForSizing(string header)
        {
            var mhzIndex = header.IndexOf(" MHz", StringComparison.Ordinal);
            if (mhzIndex <= 0) return header;

            var frequencyStart = mhzIndex - 1;
            while (frequencyStart >= 0 && (char.IsDigit(header[frequencyStart]) || header[frequencyStart] == '.'))
            {
                frequencyStart--;
            }
            frequencyStart++;

            if (frequencyStart >= mhzIndex) return header;
            return header[..frequencyStart] + "000.000" + header[mhzIndex..];
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_editMode) return;

            for (int i = _channels.Count - 1; i >= 0; i--)
            {
                var ch = _channels[i];
                var bounds = GetChipBounds(ch, i);
                if (bounds.Contains(e.Location))
                {
                    _draggingChannel = ch;
                    _dragOffset = new Point(e.X - bounds.X, e.Y - bounds.Y);
                    // Capture the mouse so fast drags cannot lose move or release events.
                    Capture = true;
                    break;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_editMode || _draggingChannel == null) return;

            _draggingChannel.HudPosition = new Point(e.X - _dragOffset.X, e.Y - _dragOffset.Y);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingChannel == null) return;

            _draggingChannel = null;
            Capture = false;
            LayoutChanged?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor); // Chroma key hidden on screen.

            for (int i = 0; i < _channels.Count; i++)
            {
                var ch = _channels[i];
                var blocks = GetBlocksForChannel(ch);
                if (blocks.Count == 0) continue;

                var bounds = GetChipBounds(ch, i);

                using var path = Theme.RoundedRect(bounds, 10);
                using var fillBrush = new SolidBrush(Theme.CardBackground);
                e.Graphics.FillPath(fillBrush, path);

                using var borderPen = new Pen(_editMode ? ch.HudColor : Theme.Border, 1f)
                {
                    DashStyle = _editMode ? DashStyle.Dash : DashStyle.Solid
                };
                e.Graphics.DrawPath(borderPen, path);

                using var accentBrush = new SolidBrush(ch.HudColor);
                e.Graphics.FillRectangle(accentBrush, bounds.X, bounds.Y + 4, 4, bounds.Height - 8);

                using var headerBrush = new SolidBrush(Theme.Text);
                int line = 0;
                foreach (var block in blocks)
                {
                    e.Graphics.DrawString(block.Header, Theme.TitleFont, headerBrush,
                        bounds.X + TextPaddingLeft, bounds.Y + ChipPadding + line * RowHeight);
                    line++;

                    using var whoBrush = new SolidBrush(block.WhoColor);
                    foreach (var whoLine in block.WhoLines)
                    {
                        e.Graphics.DrawString(whoLine, Theme.TitleFont, whoBrush,
                            bounds.X + TextPaddingLeft, bounds.Y + ChipPadding + line * RowHeight);
                        line++;
                    }
                }
            }
        }

        /// <summary>
        /// Re-asserts topmost Z-order -- occasionally needed since
        /// some full-screen exclusive games/apps can otherwise steal the
        /// top spot.
        /// </summary>
        public void ReassertTopmost()
        {
            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOMOVE);
        }
    }
}
