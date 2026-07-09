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
    /// 
    /// Full-screen, always-on-top HUD overlay (on top of any other
    /// application, including full-screen games) that draws one independent
    /// "chip" per radio -- each with its own color, its own screen position
    /// (user-draggable, persisted), shown only while that specific radio is
    /// actively transmitting or receiving. Normally fully click-through.
    /// While layout edit mode is on, only the chip rectangles themselves
    /// become clickable/draggable (via a WM_NCHITTEST override) -- every
    /// other point on screen still passes clicks through underneath, so
    /// entering edit mode never blocks interacting with the rest of the
    /// desktop, including the main app's own window.
    /// 
    public class TransmissionOverlayForm : Form
    {
        private class ChipState
        {
            public string? TxWho;
            public readonly Dictionary<string, string> RxWhos = new();
            public int UserCount;
            public bool HasContent => TxWho != null || RxWhos.Count > 0;
        }

        /// One active TX or RX entry on a chip: a header line
        /// (direction/radio/frequency) and, on its own line below it, the
        /// full callsign -- kept separate rather than a single formatted
        /// string so a long callsign gets its own dedicated line/width
        /// instead of running off the edge of a fixed-size chip.
        private readonly record struct ChipBlock(string Header, IReadOnlyList<string> WhoLines);

        private readonly List<RadioChannel> _channels;
        private readonly Dictionary<RadioChannel, ChipState> _chips = new();
        private readonly Dictionary<RadioChannel, long> _lastTxLifecycleByChannel = new();
        private readonly Dictionary<RadioChannel, Dictionary<string, long>> _lastRxLifecycleByChannel = new();
        private readonly object _lock = new();

        private const int MinChipWidth = 260;
        private const int MaxChipWidth = 520; // only truncated with an ellipsis beyond this, which should be rare
        private const int TextPaddingLeft = 12;
        private const int TextPaddingRight = 14;
        private const int RowHeight = 24;
        private const int ChipPadding = 8;
        private const int ScreenMargin = 16; // distance from screen edges for the default cascade layout
        private const int DefaultGap = 8;

        // Nominal slot height used only for the default (non-customized)
        // cascade layout math -- sized to fit a radio that's
        // simultaneously transmitting AND receiving (2 blocks x 2 lines),
        // so the default stacking doesn't jump around as chips grow/shrink.
        private const int DefaultSlotHeight = ChipPadding * 2 + 4 * RowHeight;

        private bool _editMode;
        private RadioChannel? _draggingChannel;
        private Point _dragOffset;

        /// Fired whenever the user finishes dragging a chip to a
        /// new position, so the host can persist it.
        public event Action? LayoutChanged;

        public TransmissionOverlayForm(List<RadioChannel> channels)
        {
            _channels = channels;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(1, 2, 3); // chroma-key: never used by any real drawn content
            TransparencyKey = BackColor;
            Opacity = 0.80;
            DoubleBuffered = true;

            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Bounds = bounds;
            Visible = true; // the form itself stays "visible" at all times; individual chips show/hide via painting
        }

        // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW at creation:
        // per-pixel alpha + chroma-key support, click-through by default, and
        // kept out of alt-tab/taskbar. WS_EX_TRANSPARENT is toggled live via
        // SetWindowLong when entering/exiting layout edit mode.
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

        /// Turns chip dragging on/off. While on, every radio's chip
        /// is shown (even idle ones) so it can be positioned in advance, and
        /// WS_EX_TRANSPARENT is dropped so this window can receive mouse
        /// input at all -- but WndProc's WM_NCHITTEST override below still
        /// makes every point that ISN'T over a chip pass through to
        /// whatever's underneath (other windows, including the main app's
        /// own "Done" button), so entering edit mode never blocks the rest
        /// of the screen -- only the chips themselves become clickable.
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

        /// Call when a transmission (local PTT or a received
        /// packet's start) begins. Safe to call from any thread.
        public void ShowTransmission(
            RadioChannel channel,
            bool isLocalTransmit,
            string remoteCallsign,
            string localCallsign,
            string remoteClientId = "",
            long? lifecycleSequence = null)
        {
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

        /// Call when a transmission ends. Safe to call from any thread.
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

        /// Updates the number of users currently subscribed to this
        /// radio's frequency/key group. Safe to call from any thread.
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
            catch (ObjectDisposedException) { /* closing down -- ignore */ }
            catch (InvalidOperationException) { /* handle not yet created -- ignore */ }
        }

        /// Builds the header+callsign block(s) currently active for
        /// a channel (or, in edit mode with nothing active, a single preview
        /// block), shared by both painting and hit-testing/sizing so what's
        /// drawn and what's clickable/sized are always in sync.
        private List<ChipBlock> GetBlocksForChannel(RadioChannel ch)
        {
            var blocks = new List<ChipBlock>();

            ChipState? chip;
            lock (_lock) { _chips.TryGetValue(ch, out chip); }

            var userCountSuffix = chip != null || _editMode ? $"  •  {PresenceDisplay.FormatCount(chip?.UserCount ?? 0)}" : "";

            if (chip?.TxWho != null)
                blocks.Add(new ChipBlock($"TX ▶  {ch.Name}  {ch.Frequency:0.000} MHz{userCountSuffix}", new[] { chip.TxWho }));
            if (chip?.RxWhos.Count > 0)
                blocks.Add(new ChipBlock($"RX ◀  {ch.Name}  {ch.Frequency:0.000} MHz{userCountSuffix}", PackWhoLines(chip.RxWhos.Values)));

            if (blocks.Count == 0 && _editMode)
                blocks.Add(new ChipBlock($"TX ▶  {ch.Name}  {ch.Frequency:0.000} MHz{userCountSuffix}", new[] { "Preview" }));

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

        /// Computes this channel's chip rectangle right now,
        /// sizing it to the widest currently visible radio chip so the three
        /// HUD popups stay visually consistent (falling back to a zero-size,
        /// off-screen rect if there's nothing to show and we're not in edit
        /// mode -- callers should check GetBlocksForChannel first if they
        /// need to know whether anything is actually visible).
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
                    // Explicit capture: once a drag starts, keep receiving
                    // mouse move/up for this window regardless of where the
                    // cursor is or what WM_NCHITTEST would say for that
                    // point -- otherwise a fast drag that briefly moves
                    // outside the (moving) chip's own bounds could lose
                    // mouse input mid-drag.
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
            e.Graphics.Clear(BackColor); // chroma-key color -- invisible on screen

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
                using var whoBrush = new SolidBrush(Theme.MutedText);

                int line = 0;
                foreach (var block in blocks)
                {
                    e.Graphics.DrawString(block.Header, Theme.TitleFont, headerBrush,
                        bounds.X + TextPaddingLeft, bounds.Y + ChipPadding + line * RowHeight);
                    line++;

                    foreach (var whoLine in block.WhoLines)
                    {
                        e.Graphics.DrawString(whoLine, Theme.TitleFont, whoBrush,
                            bounds.X + TextPaddingLeft, bounds.Y + ChipPadding + line * RowHeight);
                        line++;
                    }
                }
            }
        }

        /// Re-asserts topmost Z-order -- occasionally needed since
        /// some full-screen exclusive games/apps can otherwise steal the
        /// top spot.
        public void ReassertTopmost()
        {
            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOMOVE);
        }
    }
}
