using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using RadioRelay.Client.Diagnostics;
using RadioRelay.Client.AudioEngineNs;
using RadioRelay.Client.Input;
using RadioRelay.Client.Networking;
using RadioRelay.Client.Radio;
using RadioRelay.Client.UI;
using RadioRelay.Shared.Diagnostics;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Client
{
    public class MainForm : ModernWindowForm
    {
        private class RadioRow
        {
            public required RadioChannel Channel;
            public required TextBox NameBox;
            public required NumericTextBox Freq;
            public required ModernSlider Vol;
            public required DarkComboBox Ear;
            public required TextBox Passcode;
            public required Button PttPrimaryButton;
            public required Label PttPrimaryLabel;
            public required Button PttSecondaryButton;
            public required Label PttSecondaryLabel;
            public required Button ColorButton;
            public required Label UserCountLabel;
            public required Panel Rail;
            public required Label StatusBadge;
            public required Label VolumeValueLabel;
            public required Label EncryptStateLabel;
        }

        private sealed class DarkInputHost : Panel
        {
            private bool _childFocused;

            public bool ShowBorder { get; set; } = true;

            public bool ChildFocused
            {
                get => _childFocused;
                set
                {
                    if (_childFocused == value) return;
                    _childFocused = value;
                    Invalidate();
                }
            }

            public DarkInputHost()
            {
                // This host is the only control responsible for the field
                // background and border. The native editor/selector inside it
                // is deliberately borderless and inset from every edge.
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.Opaque |
                    ControlStyles.UserPaint,
                    true);

                BackColor = Theme.CardBackground;
                Margin = Padding.Empty;
                MinimumSize = new Size(1, 26);
                TabStop = false;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                // Keep the complete shape inside the host. The field is not
                // given a Region and does not rely on parent transparency.
                // This makes the result independent of native TextBox,
                // native editor, ComboBox, and TableLayoutPanel clipping.
                var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                using var path = Theme.RoundedRect(bounds, 6);
                using var fieldBrush = new SolidBrush(Theme.FieldBackground);
                e.Graphics.FillPath(fieldBrush, path);

                if (!ShowBorder)
                    return;

                using var pen = new Pen(ChildFocused ? Theme.AccentBlue : Theme.Border, ChildFocused ? 1.5f : 1f)
                {
                    Alignment = System.Drawing.Drawing2D.PenAlignment.Inset
                };
                e.Graphics.DrawPath(pen, path);
            }
        }



        private readonly AppSettings _settings;
        private readonly List<RadioChannel> _channels;

        private AudioEngine? _audioEngine;
        private RelayClient? _relayClient;
        private readonly PttInputManager _pttInput = new();
        private readonly Dictionary<RadioChannel, System.Threading.Timer> _pttReleaseTimers = new();
        private int _pttReleaseDelayMs = 200;

        private readonly TransmissionOverlayForm _overlay;
        private readonly Guid _identity = Guid.NewGuid();
        private bool _hudEditMode;
        private bool _connectionEstablished;
        private bool _controlLockEnabled;
        private readonly CancellationTokenSource _updateCheckCancellation = new();
        private string? _availableUpdateUrl;
        private bool _updateCheckStarted;

        private readonly TextBox _serverBox = new() { Text = "127.0.0.1" };
        private readonly NumericTextBox _portBox = new() { Minimum = 1, Maximum = 65535, Value = 5060 };
        private readonly TextBox _serverPasswordBox = new() { Text = "", Width = 120, UseSystemPasswordChar = true };
        private readonly ModernButton _connectButton = new() { Text = "Connect", Emphasized = true, BackColor = Theme.AccentBlue, ForeColor = Color.White };
        private readonly TextBox _callsignBox = new() { Text = "", MaxLength = 20 };
        private readonly Label _statusLabel = new() { Text = "Disconnected", AutoSize = true, ForeColor = Theme.AccentRed };
        private readonly Label _versionLabel = new() { Text = ApplicationVersion.DisplayName, AutoSize = true, ForeColor = Theme.MutedText };
        private readonly NumericTextBox _pttReleaseDelayBox = new() { Minimum = 0, Maximum = 2000, Value = 200, Width = 70 };

        private readonly DarkComboBox _inputDeviceBox = new() { DropDownWidth = 360 };
        private readonly DarkComboBox _outputDeviceBox = new() { DropDownWidth = 360 };
        private readonly ModernSlider _inputGainSlider = new() { Minimum = 0, Maximum = 300, Value = 100 };
        private readonly ModernSlider _inputClickVolSlider = new() { Minimum = 0, Maximum = 100, Value = 100 };
        private readonly ModernSlider _talkOverVolSlider = new() { Minimum = 0, Maximum = 100, Value = 100 };
        private readonly ModernSlider _outputClickVolSlider = new() { Minimum = 0, Maximum = 100, Value = 100 };

        private readonly ModernButton _hudLayoutButton = new() { Text = "Customize HUD" };
        private readonly ModernButton _controlLockButton = new() { Text = "Lock Controls" };
        private readonly ModernButton _exportSettingsButton = new() { Text = "Export Settings" };
        private readonly ModernButton _importSettingsButton = new() { Text = "Import Settings" };

        private readonly ListBox _logBox = new() { IntegralHeight = false };
        private readonly TableLayoutPanel _page = new() { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly ModernScrollHost _scrollHost = new()
        {
            Dock = DockStyle.Fill,
            MinimumContentWidth = MainFormLayoutPolicy.MaxContentWidth,
            MaximumContentWidth = MainFormLayoutPolicy.MaxContentWidth,
            ContentPadding = new Padding(
                MainFormLayoutPolicy.HorizontalMargin,
                14,
                MainFormLayoutPolicy.HorizontalMargin,
                MainFormLayoutPolicy.HorizontalMargin)
        };
        private readonly RadioActivityTracker _activityTracker = new();
        private bool _layoutInProgress;

        private readonly List<RadioRow> _radioRows = new();

        // Local aliases keep the layout construction readable while the
        // shared policy remains the single source of truth for testable size
        // budgets and the responsive main-window geometry.
        private const int PreferredWindowWidth = MainFormLayoutPolicy.FixedWindowWidth;
        private const int PreferredWindowHeight = MainFormLayoutPolicy.FixedWindowHeight;
        private const int PreferredContentWidth = MainFormLayoutPolicy.MaxContentWidth;
        private const int CardPadding = 18;
        private const int Gap = 8;
        private const int FieldCaptionHeight = 16;
        private const int SafeRightMargin = 0;
        private const int RailWidth = 4;
        private const int CompactRadioCardHeight = MainFormLayoutPolicy.RadioCardHeight;
        private const int TopCardHeight = MainFormLayoutPolicy.ConnectionStripHeight;
        private const int ToolbarCardHeight = MainFormLayoutPolicy.OperationsStripHeight;
        private const int LogCardHeight = MainFormLayoutPolicy.LogHeight;

        public MainForm()
        {
            _settings = AppSettings.Load();
            _channels = BuildChannelsFromSettings();
            _overlay = new TransmissionOverlayForm(_channels);

            Text = ApplicationVersion.DisplayName;
            TitleBar.Title = "RadioRelay";
            TitleBar.Subtitle = ApplicationVersion.Current;
            TitleBar.MaximizeAvailable = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            MaximizeBox = false;
            CanResizeHorizontally = false;
            StartPosition = FormStartPosition.CenterScreen;
            Width = PreferredWindowWidth;
            Height = PreferredWindowHeight;
            MinimumSize = new Size(PreferredWindowWidth, MainFormLayoutPolicy.MinimumWindowHeight);
            MaximumSize = new Size(PreferredWindowWidth, MainFormLayoutPolicy.MaximumWindowHeight);
            AutoScroll = false;
            Font = Theme.BodyFont;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            ContentHost.Controls.Add(_scrollHost);
            BuildUi();
            WireEvents();
            ApplySavedGlobalSettings();
            ApplySavedPttBindings();
        }

        private List<RadioChannel> BuildChannelsFromSettings()
        {
            var defaults = new List<RadioChannel>
            {
                new RadioChannel { Name = "RADIO 1", Frequency = 251.000f, HudColor = Color.FromArgb(90, 160, 235) },
                new RadioChannel { Name = "RADIO 2", Frequency = 305.000f, HudColor = Color.FromArgb(180, 120, 235) },
                new RadioChannel { Name = "RADIO 3", Frequency = 100.000f, HudColor = Color.FromArgb(90, 210, 170) }
            };

            foreach (var ch in defaults)
            {
                ApplySavedRadioSettings(ch);
            }
            return defaults;
        }

        private void ApplySavedRadioSettings(RadioChannel ch)
        {
            // Fall back to a legacy "INTERCOM" saved entry for the
            // renamed third radio, so upgrading doesn't silently wipe
            // out someone's already-configured frequency/PTT
            // bindings/HUD position for it.
            var saved = _settings.Radios.Find(r => r.Name == ch.Name)
                ?? (ch.Name == "RADIO 3" ? _settings.Radios.Find(r => r.Name == "INTERCOM") : null);
            if (saved == null) return;
            ch.LocalName = saved.LocalName;
            ch.Frequency = Math.Clamp(saved.Frequency, 2f, 999f);
            ch.Volume = Math.Clamp(saved.Volume, 0f, 1f);
            ch.Ear = saved.Ear;
            ch.Passcode = saved.Passcode;
            ch.HudColor = Color.FromArgb(saved.HudColorArgb);
            if (saved.HudX.HasValue && saved.HudY.HasValue)
                ch.HudPosition = new Point(saved.HudX.Value, saved.HudY.Value);
        }

        private void ApplySavedRadioSettingsToExistingChannels()
        {
            foreach (var ch in _channels) ApplySavedRadioSettings(ch);
        }

        // ---------- Small theming helpers ----------

        private void AddPageRow(Control control, int bottomMargin = 10)
        {
            control.Margin = new Padding(0, 0, 0, bottomMargin);
            _page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _page.Controls.Add(control, 0, _page.RowCount++);
        }

        internal static float GetRadioNameFontSize(string text, int availableWidth)
        {
            const float minimumSize = 4f;
            const int textBoxInsetAndCaret = 14;
            var normalSize = Theme.RadioTitleFont.Size;
            if (string.IsNullOrEmpty(text) || availableWidth <= 0) return normalSize;

            var safeWidth = Math.Max(1, availableWidth - textBoxInsetAndCaret);

            var measuredWidth = TextRenderer.MeasureText(
                text,
                Theme.RadioTitleFont,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (measuredWidth <= safeWidth) return normalSize;

            var size = Math.Clamp(normalSize * safeWidth / measuredWidth, minimumSize, normalSize);
            while (size > minimumSize)
            {
                using var candidate = new Font(
                    Theme.RadioTitleFont.FontFamily,
                    size,
                    Theme.RadioTitleFont.Style,
                    GraphicsUnit.Point);
                var candidateWidth = TextRenderer.MeasureText(
                    text,
                    candidate,
                    Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                if (candidateWidth <= safeWidth) return size;
                size = Math.Max(minimumSize, size - 0.25f);
            }

            return minimumSize;
        }

        private static void FitRadioNameFont(TextBox nameBox)
        {
            var availableWidth = Math.Max(1, nameBox.ClientSize.Width - 2);
            var size = GetRadioNameFontSize(nameBox.Text, availableWidth);
            if (Math.Abs(nameBox.Font.Size - size) < 0.05f) return;

            var previousFont = nameBox.Font;
            nameBox.Font = Math.Abs(size - Theme.RadioTitleFont.Size) < 0.05f
                ? Theme.RadioTitleFont
                : new Font(Theme.RadioTitleFont.FontFamily, size, Theme.RadioTitleFont.Style, GraphicsUnit.Point);

            if (!ReferenceEquals(previousFont, Theme.RadioTitleFont)) previousFont.Dispose();
        }

        private Panel CreateStrip(int height)
        {
            var strip = new TableLayoutPanel
            {
                Height = height,
                Width = _page.Width,
                BackColor = Theme.CardBackground,
                ColumnCount = 1,
                RowCount = 1,
                Padding = new Padding(14, 10, 14, 10)
            };
            strip.Paint += PaintBorder;
            return strip;
        }

        private Panel CreateRadioCard(RadioChannel channel, out TableLayoutPanel body, out Label statusBadge, out Panel rail, out Label userCountHeaderLabel)
        {
            var card = new ModernPanel
            {
                Width = _page.Width,
                Height = CompactRadioCardHeight,
                FillColor = Theme.CardBackground,
                BorderColor = Theme.Border,
                CornerRadius = 12,
                Padding = new Padding(CardPadding, 10, CardPadding, 10)
            };

            rail = new Panel
            {
                Dock = DockStyle.Right,
                Width = RailWidth,
                BackColor = Theme.SoftBorder,
                Margin = new Padding(0)
            };
            card.Controls.Add(rail);

            body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = Padding.Empty
            };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(body);
            rail.BringToFront();

            userCountHeaderLabel = new Label
            {
                Text = PresenceDisplay.FormatCount(0),
                AutoSize = false,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = Theme.MutedText,
                Font = Theme.SmallMonoFont,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };

            statusBadge = CreateBadge("IDLE", Theme.FaintText, Theme.SoftBorder);
            return card;
        }

        private static Label CreateBadge(string text, Color foreColor, Color borderColor)
        {
            return new StatusBadge
            {
                Text = text,
                Width = MainFormLayoutPolicy.RadioActivityBadgeWidth,
                Height = 24,
                ForeColor = foreColor,
                BackColor = borderColor,
                Font = Theme.SmallMonoFont,
                Padding = new Padding(6, 2, 6, 2),
                Margin = new Padding(0)
            };
        }

        private static Label CreateFieldLabel(string text) => new()
        {
            Text = text.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = Theme.HeaderText,
            Font = Theme.SmallMonoFont,
            Margin = new Padding(0, 0, 0, 3)
        };

        private static Label CreateLabel(string text, int x = 0, int y = 0, bool muted = false) => new()
        {
            Text = text,
            Left = x,
            Top = y,
            AutoSize = true,
            ForeColor = muted ? Theme.MutedText : Theme.Text,
            Font = Theme.BodyFont
        };

        private static void PaintBorder(object? sender, PaintEventArgs e)
        {
            if (sender is not Control c) return;
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, c.Width - 1, c.Height - 1);
        }

        private static void StyleField(Control c)
        {
            c.BackColor = Theme.FieldBackground;
            c.ForeColor = Theme.Text;
            c.Font = Theme.MonoFont;
        }

        private static void StyleCompactInput(Control c)
        {
            StyleField(c);
            c.Margin = Padding.Empty;
            c.Dock = DockStyle.None;
            // The host performs the one and only bounds calculation. Leaving
            // left/right anchoring enabled lets WinForms resize the native
            // child a second time and can push its right edge beyond the host.
            c.Anchor = AnchorStyles.None;

            if (c is TextBox textBox)
            {
                // Draw text inside a custom dark host instead of relying on
                // the native white WinForms border, which was clipping on the
                // right edge at this compact size.
                textBox.BorderStyle = BorderStyle.None;
                textBox.Multiline = false;
            }
            else if (c is ComboBox combo)
            {
                combo.BackColor = Theme.FieldBackground;
                combo.ForeColor = Theme.Text;
                combo.FlatStyle = FlatStyle.Flat;
                combo.IntegralHeight = false;
                combo.ItemHeight = Math.Max(combo.ItemHeight, 16);
                combo.Margin = new Padding(0);
            }
            else if (c is DarkComboBox darkCombo)
            {
                darkCombo.BackColor = Theme.FieldBackground;
                darkCombo.ForeColor = Theme.Text;
                darkCombo.Margin = new Padding(0);
            }
        }

        private static Panel CreateCompactInputHost(Control control)
        {
            StyleCompactInput(control);

            var host = new DarkInputHost
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.CardBackground,
                Margin = Padding.Empty,
                ShowBorder = true,
                Padding = control is ComboBox or DarkComboBox
                    ? new Padding(8, 4, 9, 4)
                    : new Padding(9, 4, 9, 4)
            };

            host.Controls.Add(control);
            host.Layout += (_, _) => LayoutCompactInputChild(host, control);
            control.Enter += (_, _) => host.ChildFocused = true;
            control.Leave += (_, _) => host.ChildFocused = false;
            LayoutCompactInputChild(host, control);
            return host;
        }

        private static void LayoutCompactInputChild(Control host, Control control)
        {
            var content = new Rectangle(
                host.Padding.Left,
                host.Padding.Top,
                Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal),
                Math.Max(1, host.ClientSize.Height - host.Padding.Vertical));

            // Single-line native editors do not honor arbitrary heights. Keep
            // their preferred height and center them, while custom selectors
            // can use the complete inset content rectangle.
            if (control is TextBox or ComboBox)
            {
                int preferredHeight = Math.Min(content.Height, Math.Max(1, control.PreferredSize.Height));
                int top = content.Top + Math.Max(0, (content.Height - preferredHeight) / 2);
                control.SetBounds(content.Left, top, content.Width, preferredHeight);
            }
            else
            {
                control.SetBounds(content.Left, content.Top, content.Width, content.Height);
            }

        }

        private static void StyleSlider(ModernSlider slider)
        {
            slider.BackColor = Color.Transparent;
            slider.Height = 28;
            slider.Margin = new Padding(0);
            slider.SmallChange = 1;
            slider.LargeChange = 5;
            slider.AccentColor = Theme.AccentGreen;
        }

        private static TableLayoutPanel CreateSliderCluster(string label, ModernSlider slider, Label? valueLabel = null, int width = 150)
        {
            StyleSlider(slider);
            slider.Width = width;
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = valueLabel == null ? 1 : 2,
                RowCount = 2,
                Margin = new Padding(0, 0, 14, 0)
            };
            panel.Controls.Add(CreateFieldLabel(label), 0, 0);
            if (valueLabel != null)
            {
                valueLabel.Font = Theme.SmallMonoFont;
                valueLabel.ForeColor = Theme.MutedText;
                valueLabel.Anchor = AnchorStyles.Right;
                panel.Controls.Add(valueLabel, 1, 0);
            }
            panel.Controls.Add(slider, 0, 1);
            if (valueLabel != null) panel.SetColumnSpan(slider, 2);
            return panel;
        }

        private static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = b.ForeColor == SystemColors.ControlText ? Theme.Text : b.ForeColor;
            b.Font = Theme.ButtonFont;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.BorderSize = b is ModernButton ? 0 : 1;

            if (b is ModernButton modern)
            {
                modern.BorderColor = Theme.Border;
                if (!modern.Emphasized) modern.BackColor = Theme.RaisedBackground;
            }
            else
            {
                b.BackColor = Theme.RaisedBackground;
            }
        }

        private static int ButtonWidthFor(string text, int minimum = 90)
        {
            return Math.Max(minimum, TextRenderer.MeasureText(text, Theme.SmallMonoFont).Width + 28);
        }

        private static Label CreateCaption(string text)
        {
            var caption = CreateFieldLabel(text);
            caption.AutoSize = false;
            caption.Dock = DockStyle.Fill;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.Margin = new Padding(0, 0, 0, 3);
            return caption;
        }

        private static TableLayoutPanel CreateSectionHeader(string title, string subtitle)
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var titleLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Theme.Text,
                Font = Theme.SectionTitleFont,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            var subtitleLabel = new Label
            {
                Text = subtitle.ToUpperInvariant(),
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                ForeColor = Theme.HeaderText,
                Font = Theme.SmallMonoFont,
                Margin = new Padding(10, 0, 0, 0)
            };
            header.Controls.Add(titleLabel, 0, 0);
            header.Controls.Add(subtitleLabel, 1, 0);
            return header;
        }

        private static TableLayoutPanel CreateFixedField(string label, Control control, int width)
        {
            var panel = CreateFieldContainer(width);
            control.Margin = Padding.Empty;
            if (control is Button)
            {
                // Preserve compact button dimensions and align the HUD swatch
                // directly beneath its left-aligned caption.
                control.Dock = DockStyle.None;
                control.Anchor = AnchorStyles.Left;
            }
            else
            {
                control.Dock = DockStyle.Fill;
            }
            if (control is Label valueLabel)
            {
                valueLabel.AutoSize = false;
                valueLabel.TextAlign = ContentAlignment.MiddleLeft;
            }
            panel.Controls.Add(CreateCaption(label), 0, 0);
            panel.Controls.Add(control, 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateCompactField(string label, Control control, int width)
        {
            var panel = CreateFieldContainer(width);
            control.AccessibleName = label;
            panel.Controls.Add(CreateCaption(label), 0, 0);
            panel.Controls.Add(CreateCompactInputHost(control), 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateFillField(string label, Control control)
        {
            var panel = CreateFieldContainer(1);
            panel.Dock = DockStyle.Fill;
            control.AccessibleName = label;
            panel.Controls.Add(CreateCaption(label), 0, 0);
            panel.Controls.Add(CreateCompactInputHost(control), 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateFieldContainer(int width)
        {
            var panel = new TableLayoutPanel
            {
                Width = width,
                Height = 44,
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            // This explicit 100% column is the important part. When no
            // ColumnStyle is supplied, TableLayoutPanel treats the column as
            // AutoSize. A child Panel's default preferred width is 200px, so
            // the field became wider than its fixed parent and its right edge
            // was simply clipped. That is why TextBox, DarkComboBox, and HUD
            // swatch controls all appeared to lose the same right border.
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, FieldCaptionHeight));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            return panel;
        }

        private static TableLayoutPanel CreateSliderCell(string label, ModernSlider slider, Label? valueLabel = null)
        {
            StyleSlider(slider);
            slider.Margin = new Padding(0);
            slider.Dock = DockStyle.Fill;
            slider.AccessibleName = label;

            valueLabel ??= new Label();
            valueLabel.Text = $"{slider.Value}%";
            slider.ValueChanged += (_, _) => valueLabel.Text = $"{slider.Value}%";

            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));

            header.Controls.Add(CreateCaption(label), 0, 0);
            valueLabel.AutoSize = false;
            valueLabel.Dock = DockStyle.Fill;
            valueLabel.Font = Theme.SmallMonoFont;
            valueLabel.ForeColor = Theme.MutedText;
            valueLabel.TextAlign = ContentAlignment.MiddleRight;
            valueLabel.Margin = new Padding(0);
            header.Controls.Add(valueLabel, 1, 0);

            var sliderHost = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(2, 3, 2, 0),
                BackColor = Color.Transparent
            };
            sliderHost.Controls.Add(slider);

            panel.Controls.Add(header, 0, 0);
            panel.Controls.Add(sliderHost, 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateServerRow(
            TextBox serverBox,
            NumericTextBox portBox,
            TextBox passwordBox,
            Button connectButton,
            Label statusLabel,
            Label versionLabel)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 10,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            connectButton.Width = 110;
            connectButton.Height = 32;
            connectButton.Margin = new Padding(0, FieldCaptionHeight, 0, 0);
            connectButton.Dock = DockStyle.Fill;

            statusLabel.AutoSize = false;
            statusLabel.Font = Theme.SmallMonoFont;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.AutoEllipsis = true;
            statusLabel.Margin = new Padding(0, FieldCaptionHeight, 0, 0);
            statusLabel.Dock = DockStyle.Fill;

            versionLabel.AutoSize = false;
            versionLabel.Font = Theme.SmallMonoFont;
            versionLabel.TextAlign = ContentAlignment.MiddleRight;
            versionLabel.AutoEllipsis = true;
            versionLabel.Margin = new Padding(Gap, FieldCaptionHeight, 0, 0);
            versionLabel.Dock = DockStyle.Fill;

            row.Controls.Add(CreateCompactField("Server", serverBox, 144), 0, 0);
            row.Controls.Add(CreateCompactField("Port", portBox, 72), 2, 0);
            row.Controls.Add(CreateCompactField("Password", passwordBox, 100), 4, 0);
            row.Controls.Add(connectButton, 6, 0);
            row.Controls.Add(statusLabel, 8, 0);
            row.Controls.Add(versionLabel, 9, 0);
            return row;
        }

        private static TableLayoutPanel CreateDeviceRow(TextBox callsignBox, DarkComboBox inputDeviceBox, DarkComboBox outputDeviceBox)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            row.Controls.Add(CreateCompactField("Callsign", callsignBox, 140), 0, 0);
            row.Controls.Add(CreateFillField("Input", inputDeviceBox), 2, 0);
            row.Controls.Add(CreateFillField("Output", outputDeviceBox), 4, 0);
            return row;
        }

        private static TableLayoutPanel CreateTopSliderRow(
            ModernSlider inputGain,
            ModernSlider txClick,
            ModernSlider rxClick,
            ModernSlider talkover)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 7,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                if (i < 3) row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            }

            row.Controls.Add(CreateSliderCell("Input gain", inputGain), 0, 0);
            row.Controls.Add(CreateSliderCell("TX click", txClick), 2, 0);
            row.Controls.Add(CreateSliderCell("RX click", rxClick), 4, 0);
            row.Controls.Add(CreateSliderCell("Talkover", talkover), 6, 0);
            return row;
        }

        private static TableLayoutPanel CreateTopSliderPairRow(
            ModernSlider leftSlider,
            ModernSlider rightSlider,
            string leftLabel,
            string rightLabel)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap + 8));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.Controls.Add(CreateSliderCell(leftLabel, leftSlider), 0, 0);
            row.Controls.Add(CreateSliderCell(rightLabel, rightSlider), 2, 0);
            return row;
        }

        private static TableLayoutPanel CreateToolbarRows(
            NumericTextBox pttReleaseDelayBox,
            Button controlLockButton,
            Button hudLayoutButton,
            Button exportSettingsButton,
            Button importSettingsButton)
        {
            const int buttonHeight = 32;
            foreach (var button in new[] { controlLockButton, hudLayoutButton, exportSettingsButton, importSettingsButton })
            {
                button.Height = buttonHeight;
                button.Dock = DockStyle.Fill;
                button.Margin = new Padding(0, FieldCaptionHeight, 0, 0);
            }

            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 9,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            row.Controls.Add(CreateCompactField("PTT ms", pttReleaseDelayBox, 86), 0, 0);
            row.Controls.Add(controlLockButton, 2, 0);
            row.Controls.Add(hudLayoutButton, 4, 0);
            row.Controls.Add(exportSettingsButton, 6, 0);
            row.Controls.Add(importSettingsButton, 8, 0);
            return row;
        }

        private static TableLayoutPanel CreateRadioHeaderRow(
            TextBox nameBox,
            Label userCountLabel,
            TextBox passcode,
            Label encryptState,
            Button colorButton,
            Label statusBadge)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 11,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MainFormLayoutPolicy.RadioTitleColumnWidth));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

            userCountLabel.Margin = new Padding(0);
            userCountLabel.TextAlign = ContentAlignment.MiddleLeft;

            passcode.Width = 100;
            passcode.Height = 22;
            encryptState.AutoSize = false;
            encryptState.Font = Theme.SmallMonoFont;
            encryptState.TextAlign = ContentAlignment.MiddleLeft;
            encryptState.Margin = new Padding(0, 0, 2, 0);

            colorButton.Width = 42;
            colorButton.Height = 24;
            colorButton.Margin = new Padding(0, 0, 2, 0);

            statusBadge.Dock = DockStyle.None;
            statusBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            statusBadge.Margin = Padding.Empty;
            statusBadge.Width = MainFormLayoutPolicy.RadioActivityBadgeColumnWidth;
            if (statusBadge is StatusBadge badge)
                badge.FlatRightEdge = true;

            row.Controls.Add(nameBox, 0, 0);
            row.Controls.Add(CreateFixedField("Users", userCountLabel, 76), 2, 0);
            row.Controls.Add(CreateCompactField("Key", passcode, 120), 4, 0);
            row.Controls.Add(CreateFixedField("", encryptState, 76), 6, 0);
            row.Controls.Add(CreateFixedField("HUD", colorButton, 50), 8, 0);
            row.Controls.Add(statusBadge, 10, 0);
            return row;
        }

        private static TableLayoutPanel CreateRadioSettingsRow(
            NumericTextBox freq,
            ModernSlider vol,
            Label volValue,
            DarkComboBox ear)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            // Intentional right spacer keeps the output selector clear of
            // the activity rail at the far-right edge of the card.
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));

            row.Controls.Add(CreateCompactField("Frequency (MHz)", freq, 108), 0, 0);
            row.Controls.Add(CreateSliderCell("VOL", vol, volValue), 2, 0);
            row.Controls.Add(CreateCompactField("Output", ear, 104), 4, 0);
            return row;
        }

        private static TableLayoutPanel CreateField(string label, Control control, int width = 0)
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 14, 0)
            };
            control.Width = width > 0 ? width : control.Width;
            control.Height = Math.Max(control.Height, 28);
            panel.Controls.Add(CreateFieldLabel(label), 0, 0);
            panel.Controls.Add(control, 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateRow(params Control[] controls) => CreateRow(true, controls);

        // Builds a single-line row that can never wrap: extra controls that
        // wouldn't fit inside a wrapping FlowLayoutPanel used to get pushed
        // onto an invisible second line and clipped by the row's fixed
        // height (that's what was hiding the HUD swatch, output device,
        // talkover slider, and export/import buttons). A TableLayoutPanel
        // with one auto-sized column per control plus a trailing percent
        // filler column keeps every control on the one visible row.
        private static TableLayoutPanel CreateRow(bool autoStyleMargins, params Control[] controls)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = controls.Length + 1,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < controls.Length; i++)
            {
                var control = controls[i];
                if (autoStyleMargins)
                {
                    control.Margin = control switch
                    {
                        Label => new Padding(0, 5, 8, 0),
                        Button => new Padding(0, 0, 10, 0),
                        ModernSlider => new Padding(0, 0, 14, 0),
                        _ => control.Margin
                    };
                }
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.Controls.Add(control, i, 0);
            }
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return row;
        }

        internal static string CompactPttDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "Unbound";
            if (displayName.StartsWith("Keyboard: ", StringComparison.OrdinalIgnoreCase))
                return displayName[10..];
            if (displayName.StartsWith("Joystick button ", StringComparison.OrdinalIgnoreCase))
                return "Joy Btn " + displayName[16..];
            return displayName;
        }

        private static TableLayoutPanel CreatePttRow(
            Label pttPrimaryLabel,
            Button pttPrimaryButton,
            Label pttSecondaryLabel,
            Button pttSecondaryButton)
        {
            // The PTT A / PTT B captions are the clickable capture buttons
            // now, which removes the separate Set buttons and keeps each
            // binding row compact without changing the event wiring.
            const int pttRowHeight = 34;
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap + 8));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, pttRowHeight));

            pttPrimaryButton.Text = "PTT A";
            pttSecondaryButton.Text = "PTT B";
            foreach (var button in new[] { pttPrimaryButton, pttSecondaryButton })
            {
                button.Dock = DockStyle.Fill;
                button.Height = 30;
                button.Margin = new Padding(0, 2, 0, 2);
                button.TextAlign = ContentAlignment.MiddleCenter;
            }

            foreach (var label in new[] { pttPrimaryLabel, pttSecondaryLabel })
            {
                label.AutoSize = false;
                label.Height = pttRowHeight;
                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.AutoEllipsis = true;
                label.Margin = new Padding(8, 0, 6, 0);
            }

            row.Controls.Add(pttPrimaryButton, 0, 0);
            row.Controls.Add(pttPrimaryLabel, 1, 0);
            row.Controls.Add(new Panel { Dock = DockStyle.Fill }, 2, 0);
            row.Controls.Add(pttSecondaryButton, 3, 0);
            row.Controls.Add(pttSecondaryLabel, 4, 0);
            return row;
        }

        private static TableLayoutPanel CreateHudField(Button colorButton)
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 14, 0)
            };
            colorButton.Margin = new Padding(0);
            panel.Controls.Add(CreateFieldLabel("HUD"), 0, 0);
            panel.Controls.Add(colorButton, 0, 1);
            return panel;
        }

        // ---------- UI construction ----------

        /// <summary>
        /// Reapplies the page width after WinForms creates or resizes the native
        /// handles. Keeping this explicit protects the AutoSize table from the
        /// blank-page regression that affected earlier responsive builds.
        /// </summary>
        private void FinalizeFixedWidthLayout()
        {
            if (_layoutInProgress || IsDisposed || Disposing)
                return;

            _layoutInProgress = true;
            try
            {
                var children = new Control[_page.Controls.Count];
                _page.Controls.CopyTo(children, 0);
                _page.SuspendLayout();
                foreach (var child in children)
                    child.SuspendLayout();
                try
                {
                    _page.Width = PreferredContentWidth;

                    // Most cards use explicit widths. Apply their final fixed
                    // geometry once after native handle and DPI creation.
                    foreach (var child in children)
                    {
                        if (child.Width != PreferredContentWidth)
                            child.Width = PreferredContentWidth;
                    }

                    _page.Visible = true;
                }
                finally
                {
                    foreach (var child in children)
                        child.ResumeLayout(performLayout: true);
                    _page.ResumeLayout(performLayout: true);
                }

                UpdateAutoScrollMinSize();
            }
            finally
            {
                _layoutInProgress = false;
            }
        }

        private void BuildUi()
        {
            _page.RowCount = 0;
            _page.RowStyles.Clear();
            _page.Controls.Clear();
            _radioRows.Clear();

            _page.Width = PreferredContentWidth;
            _page.Left = 0;
            _page.Top = 0;
            _page.BackColor = Theme.Background;
            _page.Margin = new Padding(0);
            _scrollHost.Content = _page;

            StyleField(_serverBox);
            StyleField(_portBox);
            StyleField(_serverPasswordBox);
            StyleButton(_connectButton);
            StyleField(_callsignBox);
            StyleField(_inputDeviceBox);
            StyleField(_outputDeviceBox);
            _hudLayoutButton.Text = "Customize HUD";
            StyleButton(_hudLayoutButton);
            StyleButton(_controlLockButton);
            StyleButton(_exportSettingsButton);
            StyleButton(_importSettingsButton);
            StyleField(_pttReleaseDelayBox);

            foreach (var (index, name) in AudioDeviceEnumerator.GetInputDevices())
                _inputDeviceBox.Items.Add(new DeviceItem(index, name));
            foreach (var (index, name) in AudioDeviceEnumerator.GetOutputDevices())
                _outputDeviceBox.Items.Add(new DeviceItem(index, name));

            // ---- Connection, identity, devices, and audio levels ----
            var topCard = new ModernCard
            {
                Width = _page.Width,
                Height = TopCardHeight,
                FillColor = Theme.CardBackground,
                BorderColor = Theme.Border,
                CornerRadius = 12,
                Padding = new Padding(CardPadding, 10, CardPadding, 10),
                ColumnCount = 1,
                RowCount = 5
            };
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            topCard.Controls.Add(CreateSectionHeader("Settings", "server / identity / devices"), 0, 0);
            topCard.Controls.Add(CreateServerRow(_serverBox, _portBox, _serverPasswordBox, _connectButton, _statusLabel, _versionLabel), 0, 1);
            topCard.Controls.Add(CreateDeviceRow(_callsignBox, _inputDeviceBox, _outputDeviceBox), 0, 2);
            topCard.Controls.Add(CreateTopSliderPairRow(_inputGainSlider, _inputClickVolSlider, "Input gain", "TX click"), 0, 3);
            topCard.Controls.Add(CreateTopSliderPairRow(_outputClickVolSlider, _talkOverVolSlider, "RX click", "Talkover"), 0, 4);
            AddPageRow(topCard, 10);

            // ---- Global controls ----
            var toolbar = new ModernCard
            {
                Width = _page.Width,
                Height = ToolbarCardHeight,
                FillColor = Theme.CardBackground,
                BorderColor = Theme.Border,
                CornerRadius = 12,
                Padding = new Padding(CardPadding, 8, CardPadding, 8),
                ColumnCount = 1,
                RowCount = 2
            };
            toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.Controls.Add(CreateSectionHeader("Controls", "PTT / HUD / profile"), 0, 0);
            toolbar.Controls.Add(CreateToolbarRows(_pttReleaseDelayBox, _controlLockButton, _hudLayoutButton, _exportSettingsButton, _importSettingsButton), 0, 1);
            AddPageRow(toolbar, 10);

            foreach (var ch in _channels)
            {
                var card = CreateRadioCard(ch, out var body, out var statusBadge, out var rail, out var userCountHeaderLabel);

                var nameBox = new TextBox
                {
                    Text = ch.DisplayName,
                    BorderStyle = BorderStyle.None,
                    BackColor = Theme.CardBackground,
                    ForeColor = Theme.Text,
                    Font = Theme.RadioTitleFont,
                    MaxLength = 24,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 10, 0, 8),
                    AccessibleName = $"{ch.Name} name"
                };
                nameBox.SizeChanged += (_, _) => FitRadioNameFont(nameBox);
                nameBox.Disposed += (_, _) =>
                {
                    if (!ReferenceEquals(nameBox.Font, Theme.RadioTitleFont)) nameBox.Font.Dispose();
                };

                var freq = new NumericTextBox
                {
                    Width = 108,
                    DecimalPlaces = 3,
                    Increment = 0.025m,
                    Minimum = 2,
                    Maximum = 999,
                    Value = (decimal)Math.Clamp(ch.Frequency, 2f, 999f)
                };
                StyleField(freq);

                var vol = new ModernSlider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = Math.Clamp((int)Math.Round(ch.Volume * 100), 0, 100),
                    AccentColor = ch.HudColor
                };
                var volValue = CreateLabel($"{vol.Value}%", muted: true);
                volValue.Font = Theme.SmallMonoFont;
                StyleSlider(vol);
                vol.AccentColor = ch.HudColor;

                var ear = new DarkComboBox { Width = 96, DropDownWidth = 112 };
                StyleField(ear);
                ear.Items.AddRange(new object[] { RadioEar.Left, RadioEar.Both, RadioEar.Right });
                ear.SelectedItem = ch.Ear;

                var passcode = new TextBox { Width = 108, Text = ch.Passcode, UseSystemPasswordChar = true };
                StyleField(passcode);

                var encryptStateVisual = RadioEncryptionVisualState.ForPasscode(ch.Passcode);
                var encryptState = CreateLabel(encryptStateVisual.Text);
                encryptState.ForeColor = encryptStateVisual.ForeColor;
                encryptState.Font = Theme.SmallMonoFont;
                var userCountLabel = userCountHeaderLabel;

                var colorButton = new ModernButton
                {
                    Text = string.Empty,
                    Width = 44,
                    Height = 28,
                    Emphasized = true,
                    BackColor = ch.HudColor,
                    ForeColor = Theme.Text,
                    CornerRadius = 6,
                    Margin = Padding.Empty,
                    AccessibleName = $"{ch.Name} HUD color"
                };
                StyleButton(colorButton);
                colorButton.BackColor = ch.HudColor;

                body.Controls.Add(CreateRadioHeaderRow(nameBox, userCountHeaderLabel, passcode, encryptState, colorButton, statusBadge), 0, 0);
                FitRadioNameFont(nameBox);
                body.Controls.Add(CreateRadioSettingsRow(freq, vol, volValue, ear), 0, 1);

                var pttPrimaryButton = new ModernButton { Text = "PTT A", Width = 74, Height = 30 };
                StyleButton(pttPrimaryButton);
                var pttPrimaryLabel = CreateLabel("Unbound", muted: true);
                pttPrimaryLabel.AutoSize = false;

                var pttSecondaryButton = new ModernButton { Text = "PTT B", Width = 74, Height = 30 };
                StyleButton(pttSecondaryButton);
                var pttSecondaryLabel = CreateLabel("Unbound", muted: true);
                pttSecondaryLabel.AutoSize = false;

                body.Controls.Add(CreatePttRow(pttPrimaryLabel, pttPrimaryButton, pttSecondaryLabel, pttSecondaryButton), 0, 2);

                _radioRows.Add(new RadioRow
                {
                    Channel = ch,
                    NameBox = nameBox,
                    Freq = freq,
                    Vol = vol,
                    Ear = ear,
                    Passcode = passcode,
                    PttPrimaryButton = pttPrimaryButton,
                    PttPrimaryLabel = pttPrimaryLabel,
                    PttSecondaryButton = pttSecondaryButton,
                    PttSecondaryLabel = pttSecondaryLabel,
                    ColorButton = colorButton,
                    UserCountLabel = userCountLabel,
                    Rail = rail,
                    StatusBadge = statusBadge,
                    VolumeValueLabel = volValue,
                    EncryptStateLabel = encryptState
                });
                UpdateRadioActivity(ch);
                AddPageRow(card, 10);
            }

            // ---- Activity log ----
            var logCard = new ModernCard
            {
                Width = _page.Width,
                Height = LogCardHeight,
                FillColor = Theme.CardBackground,
                BorderColor = Theme.Border,
                CornerRadius = 12,
                Padding = new Padding(CardPadding, 10, CardPadding, 12),
                ColumnCount = 1,
                RowCount = 2
            };
            logCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            logCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logCard.Controls.Add(CreateSectionHeader("Activity log", "connection / radio events"), 0, 0);

            var logHost = new DarkInputHost
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(1),
                ShowBorder = true,
                BackColor = Theme.CardBackground
            };
            _logBox.Dock = DockStyle.Fill;
            _logBox.Margin = Padding.Empty;
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.BackColor = Theme.FieldBackground;
            _logBox.ForeColor = Theme.MutedText;
            _logBox.Font = Theme.MonoFont;
            logHost.Controls.Add(_logBox);
            logCard.Controls.Add(logHost, 0, 1);
            AddPageRow(logCard, MainFormLayoutPolicy.HorizontalMargin);

            _page.PerformLayout();
            UpdateAutoScrollMinSize();
        }

        private void UpdateAutoScrollMinSize()
        {
            _scrollHost.RefreshScrollMetrics();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ClientDiagnostics.Current?.LogLifecycle(ErrorCodes.ClientFormShown, "main form shown");
            FinalizeFixedWidthLayout();
            if (!_updateCheckStarted)
            {
                _updateCheckStarted = true;
                _ = CheckForUpdatesAsync();
            }
        }

        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            try
            {
                var update = await GitHubUpdateChecker.CheckAsync(
                    ApplicationVersion.Current,
                    _updateCheckCancellation.Token);
                if (update == null || IsDisposed || Disposing) return;

                _availableUpdateUrl = update.ReleaseUrl;
                TitleBar.UpdateText = $"UPDATE {update.Version}";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        private void OpenAvailableUpdate()
        {
            if (string.IsNullOrWhiteSpace(_availableUpdateUrl)) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _availableUpdateUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogSafe($"Unable to open update page: {ex.Message}");
            }
        }

        private void WireEvents()
        {
            TitleBar.UpdateRequested += (_, _) => OpenAvailableUpdate();

            foreach (var row in _radioRows)
            {
                var localRow = row;

                localRow.NameBox.TextChanged += (_, _) =>
                {
                    localRow.Channel.LocalName = localRow.NameBox.Text;
                    FitRadioNameFont(localRow.NameBox);
                    _overlay.Invalidate();
                };

                localRow.Freq.ValueChanged += (_, _) =>
                {
                    localRow.Channel.Frequency = (float)localRow.Freq.Value;
                    ResubscribeIfConnected();
                };
                localRow.Vol.ValueChanged += (_, _) =>
                {
                    localRow.Channel.Volume = localRow.Vol.Value / 100f;
                    localRow.VolumeValueLabel.Text = $"{localRow.Vol.Value}%";
                    if (localRow.Channel.Volume <= 0f)
                    {
                        CancelPttReleaseTimer(localRow.Channel);
                        _activityTracker.RemoteEnded(localRow.Channel, "");
                        _overlay.SuppressChannel(localRow.Channel);
                    }
                    _audioEngine?.UpdateChannelVolume(localRow.Channel);
                    UpdateRadioActivity(localRow.Channel);
                };
                localRow.Ear.SelectedIndexChanged += (_, _) =>
                {
                    if (localRow.Ear.SelectedItem is RadioEar ear)
                    {
                        localRow.Channel.Ear = ear;
                        _audioEngine?.UpdateChannelEar(localRow.Channel);
                    }
                };
                localRow.Passcode.TextChanged += (_, _) =>
                {
                    localRow.Channel.Passcode = localRow.Passcode.Text;
                    UpdateEncryptState(localRow);
                    ResubscribeIfConnected();
                };

                localRow.PttPrimaryButton.Click += (_, _) =>
                    StartPttCapture(localRow.Channel, PttSlot.Primary, localRow.PttPrimaryButton, localRow.PttPrimaryLabel);
                localRow.PttSecondaryButton.Click += (_, _) =>
                    StartPttCapture(localRow.Channel, PttSlot.Secondary, localRow.PttSecondaryButton, localRow.PttSecondaryLabel);

                localRow.ColorButton.Click += (_, _) =>
                {
                    using var dialog = new ColorDialog { Color = localRow.Channel.HudColor, FullOpen = true };
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        localRow.Channel.HudColor = dialog.Color;
                        localRow.ColorButton.BackColor = dialog.Color;
                        localRow.Vol.AccentColor = dialog.Color;
                    }
                };
            }

            _connectButton.Click += (_, _) => ToggleConnection();
            _callsignBox.TextChanged += (_, _) =>
            {
                if (_audioEngine != null) _audioEngine.Callsign = _callsignBox.Text;
                if (_relayClient != null) _relayClient.Callsign = _callsignBox.Text;
                ResubscribeIfConnected();
            };
            _pttReleaseDelayBox.ValueChanged += (_, _) => _pttReleaseDelayMs = (int)_pttReleaseDelayBox.Value;

            _inputDeviceBox.SelectedIndexChanged += (_, _) =>
            {
                if (_inputDeviceBox.SelectedItem is DeviceItem item) _audioEngine?.SetInputDevice(item.Index);
            };
            _outputDeviceBox.SelectedIndexChanged += (_, _) =>
            {
                if (_outputDeviceBox.SelectedItem is DeviceItem item) _audioEngine?.SetOutputDevice(item.Index);
            };
            _inputGainSlider.ValueChanged += (_, _) =>
            {
                if (_audioEngine != null) _audioEngine.InputGain = _inputGainSlider.Value / 100f;
            };
            _inputClickVolSlider.ValueChanged += (_, _) =>
            {
                if (_audioEngine != null) _audioEngine.InputClickVolume = _inputClickVolSlider.Value / 100f;
            };
            _talkOverVolSlider.ValueChanged += (_, _) =>
            {
                if (_audioEngine != null) _audioEngine.TalkOverWarningVolume = _talkOverVolSlider.Value / 100f;
            };
            _outputClickVolSlider.ValueChanged += (_, _) =>
            {
                if (_audioEngine != null) _audioEngine.OutputClickVolume = _outputClickVolSlider.Value / 100f;
            };

            _controlLockButton.Click += (_, _) =>
            {
                _controlLockEnabled = !_controlLockEnabled;
                ApplyControlLock();
                SaveCurrentSettings();
            };

            _hudLayoutButton.Click += (_, _) =>
            {
                _hudEditMode = !_hudEditMode;
                _overlay.SetEditMode(_hudEditMode);
                _hudLayoutButton.Text = _hudEditMode ? "Done HUD" : "Customize HUD";
                if (!_hudEditMode) SaveCurrentSettings();
            };
            _overlay.LayoutChanged += SaveCurrentSettings;

            _exportSettingsButton.Click += (_, _) => ExportSettings();
            _importSettingsButton.Click += (_, _) => ImportSettings();

            _pttInput.Start();
            _pttInput.PttDown += OnPttDown;
            _pttInput.PttUp += OnPttUp;

            _audioEngine = new AudioEngine(_channels) { Callsign = _callsignBox.Text };
            _audioEngine.AudioCaptured += (_, e) => _relayClient?.SendAudio(e.Packet);
            _audioEngine.TransmissionStarted += (_, e) => OnTransmissionStarted(e);
            _audioEngine.TransmissionEnded += (_, e) => OnTransmissionEnded(e);

            // Force the overlay's native window into existence now (rather
            // than lazily on first show) so BeginInvoke from background
            // threads works correctly from the very first transmission.
            _ = _overlay.Handle;
        }

        private void OnTransmissionStarted(TransmissionEventArgs e)
        {
            if (InvokeRequired) { PostToUi(() => OnTransmissionStarted(e)); return; }
            if (!e.IsLocalTransmit && e.Channel.Volume <= 0f) return;
            if (e.Channel.Volume > 0f)
                _overlay.ShowTransmission(e.Channel, e.IsLocalTransmit, e.RemoteCallsign, _callsignBox.Text, e.RemoteClientId, e.LifecycleSequence);
            if (e.IsLocalTransmit) _activityTracker.LocalStarted(e.Channel, e.LifecycleSequence);
            else _activityTracker.RemoteStarted(e.Channel, e.RemoteClientId, e.LifecycleSequence);
            UpdateRadioActivity(e.Channel);
        }

        private void OnTransmissionEnded(TransmissionEventArgs e)
        {
            if (InvokeRequired) { PostToUi(() => OnTransmissionEnded(e)); return; }
            _overlay.HideTransmission(e.Channel, e.IsLocalTransmit, e.RemoteClientId, e.LifecycleSequence);
            if (e.IsLocalTransmit) _activityTracker.LocalEnded(e.Channel, e.LifecycleSequence);
            else _activityTracker.RemoteEnded(e.Channel, e.RemoteClientId, e.LifecycleSequence);
            UpdateRadioActivity(e.Channel);
        }

        private void UpdateRadioActivity(RadioChannel channel)
        {
            var row = _radioRows.Find(r => ReferenceEquals(r.Channel, channel));
            if (row == null) return;
            if (channel.Volume <= 0f)
            {
                row.StatusBadge.Text = "OFF";
                row.StatusBadge.ForeColor = Color.White;
                row.StatusBadge.BackColor = Theme.AccentRed;
                row.Rail.BackColor = Theme.AccentRed;
                row.StatusBadge.Invalidate();
                return;
            }

            var activity = _activityTracker.GetActivity(channel);
            if (activity == RadioActivityKind.Transmitting)
            {
                row.StatusBadge.Text = "TX";
                row.StatusBadge.ForeColor = Theme.AccentOrange;
                row.StatusBadge.BackColor = Theme.AccentOrange;
                row.Rail.BackColor = Theme.AccentOrange;
            }
            else if (activity == RadioActivityKind.Receiving)
            {
                row.StatusBadge.Text = "RX";
                row.StatusBadge.ForeColor = Theme.AccentGreen;
                row.StatusBadge.BackColor = Theme.AccentGreen;
                row.Rail.BackColor = Theme.AccentGreen;
            }
            else
            {
                row.StatusBadge.Text = "IDLE";
                row.StatusBadge.ForeColor = Theme.FaintText;
                row.StatusBadge.BackColor = Theme.SoftBorder;
                row.Rail.BackColor = Theme.SoftBorder;
            }
            row.StatusBadge.Invalidate();
        }

        private static void UpdateEncryptState(RadioRow row)
        {
            var state = RadioEncryptionVisualState.ForPasscode(row.Passcode.Text);
            row.EncryptStateLabel.Text = state.Text;
            row.EncryptStateLabel.ForeColor = state.ForeColor;
        }

        private void ApplySavedGlobalSettings()
        {
            _serverBox.Text = _settings.ServerIp;
            _serverPasswordBox.Text = _settings.ServerPassword;
            if (_settings.Port >= (int)_portBox.Minimum && _settings.Port <= (int)_portBox.Maximum)
                _portBox.Value = _settings.Port;
            _callsignBox.Text = _settings.Callsign;
            if (_audioEngine != null) _audioEngine.Callsign = _settings.Callsign;
            _controlLockEnabled = _settings.ControlLockEnabled;
            ApplyControlLock();

            if (_settings.PttReleaseDelayMs >= (int)_pttReleaseDelayBox.Minimum &&
                _settings.PttReleaseDelayMs <= (int)_pttReleaseDelayBox.Maximum)
            {
                _pttReleaseDelayBox.Value = _settings.PttReleaseDelayMs;
            }
            _pttReleaseDelayMs = (int)_pttReleaseDelayBox.Value;

            SelectDeviceItem(_inputDeviceBox, _settings.InputDeviceIndex);
            SelectDeviceItem(_outputDeviceBox, _settings.OutputDeviceIndex);
            _audioEngine?.SetInputDevice(_settings.InputDeviceIndex);
            _audioEngine?.SetOutputDevice(_settings.OutputDeviceIndex);

            _inputGainSlider.Value = Math.Clamp((int)Math.Round(_settings.InputGain * 100), _inputGainSlider.Minimum, _inputGainSlider.Maximum);
            _inputClickVolSlider.Value = Math.Clamp((int)Math.Round(_settings.InputClickVolume * 100), 0, 100);
            _talkOverVolSlider.Value = Math.Clamp((int)Math.Round(_settings.TalkOverWarningVolume * 100), 0, 100);
            _outputClickVolSlider.Value = Math.Clamp((int)Math.Round(_settings.OutputClickVolume * 100), 0, 100);
            if (_audioEngine != null)
            {
                _audioEngine.InputGain = _inputGainSlider.Value / 100f;
                _audioEngine.InputClickVolume = _inputClickVolSlider.Value / 100f;
                _audioEngine.TalkOverWarningVolume = _talkOverVolSlider.Value / 100f;
                _audioEngine.OutputClickVolume = _outputClickVolSlider.Value / 100f;
            }
        }

        private void ApplyControlLock()
        {
            var state = RadioControlLock.For(_controlLockEnabled);
            _controlLockButton.Text = state.ToggleButtonText;
            _controlLockButton.BackColor = state.IsLocked ? Theme.AccentOrange : Theme.FieldBackground;
            _controlLockButton.ForeColor = state.IsLocked ? Color.Black : Theme.Text;

            foreach (var row in _radioRows)
            {
                row.NameBox.Enabled = state.CanEditName;
                row.Freq.Enabled = state.CanEditFrequency;
                row.Passcode.Enabled = state.CanEditPasscode;
                row.PttPrimaryButton.Enabled = state.CanChangePttBinding;
                row.PttSecondaryButton.Enabled = state.CanChangePttBinding;
            }
        }

        private static void SelectDeviceItem(DarkComboBox box, int deviceIndex)
        {
            foreach (var obj in box.Items)
            {
                if (obj is DeviceItem item && item.Index == deviceIndex) { box.SelectedItem = item; return; }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private void ApplySavedPttBindings()
        {
            foreach (var row in _radioRows)
            {
                SetPttLabelUnbound(row.PttPrimaryLabel);
                SetPttLabelUnbound(row.PttSecondaryLabel);
                _pttInput.SetBinding(row.Channel, PttSlot.Primary, null);
                _pttInput.SetBinding(row.Channel, PttSlot.Secondary, null);

                var saved = _settings.Radios.Find(r => r.Name == row.Channel.Name)
                    ?? (row.Channel.Name == "RADIO 3" ? _settings.Radios.Find(r => r.Name == "INTERCOM") : null);
                if (saved == null) continue;

                ApplySavedSlot(saved.PttPrimary, row.Channel, PttSlot.Primary, row.PttPrimaryLabel);
                ApplySavedSlot(saved.PttSecondary, row.Channel, PttSlot.Secondary, row.PttSecondaryLabel);
            }
        }

        private void SetPttLabelUnbound(Label label)
        {
            label.Text = "Unbound";
            label.ForeColor = Theme.AccentOrange;
        }

        private void ApplySavedSlot(PttSlotSettings saved, RadioChannel channel, PttSlot slot, Label label)
        {
            if (saved.Type == null) return;
            var binding = new PttBinding
            {
                Type = saved.Type.Value,
                KeyCode = saved.KeyCode,
                DeviceGuid = saved.DeviceGuid,
                ButtonIndex = saved.ButtonIndex,
                DisplayName = saved.DisplayName
            };
            _pttInput.SetBinding(channel, slot, binding);
            label.Text = CompactPttDisplayName(binding.DisplayName);
            label.ForeColor = Theme.AccentGreen;
        }

        private void SaveCurrentSettings()
        {
            _settings.ServerIp = _serverBox.Text;
            _settings.Port = (int)_portBox.Value;
            _settings.ServerPassword = _serverPasswordBox.Text;
            _settings.Callsign = _callsignBox.Text;
            _settings.PttReleaseDelayMs = _pttReleaseDelayMs;
            _settings.ControlLockEnabled = _controlLockEnabled;

            _settings.InputDeviceIndex = _inputDeviceBox.SelectedItem is DeviceItem inItem ? inItem.Index : -1;
            _settings.OutputDeviceIndex = _outputDeviceBox.SelectedItem is DeviceItem outItem ? outItem.Index : -1;
            _settings.InputGain = _inputGainSlider.Value / 100f;
            _settings.InputClickVolume = _inputClickVolSlider.Value / 100f;
            _settings.TalkOverWarningVolume = _talkOverVolSlider.Value / 100f;
            _settings.OutputClickVolume = _outputClickVolSlider.Value / 100f;

            _settings.Radios.Clear();
            foreach (var row in _radioRows)
            {
                var primary = _pttInput.GetBinding(row.Channel, PttSlot.Primary);
                var secondary = _pttInput.GetBinding(row.Channel, PttSlot.Secondary);

                _settings.Radios.Add(new RadioSettings
                {
                    Name = row.Channel.Name,
                    LocalName = row.Channel.LocalName,
                    Frequency = row.Channel.Frequency,
                    Volume = row.Channel.Volume,
                    Ear = row.Channel.Ear,
                    Passcode = row.Channel.Passcode,
                    HudColorArgb = row.Channel.HudColor.ToArgb(),
                    HudX = row.Channel.HudPosition?.X,
                    HudY = row.Channel.HudPosition?.Y,
                    PttPrimary = ToSlotSettings(primary),
                    PttSecondary = ToSlotSettings(secondary)
                });
            }

            _settings.Save();
        }

        private void ExportSettings()
        {
            SaveCurrentSettings();
            using var dialog = new SaveFileDialog
            {
                Title = "Export RadioRelay Settings",
                Filter = "RadioRelay settings (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "json",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = AppSettings.ExportFileName
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var path = _settings.ExportToFile(dialog.FileName);
                LogSafe($"Settings exported to {path}");
            }
            catch (Exception ex)
            {
                LogSafe($"Settings export failed: {ex.Message}");
                ClientDiagnostics.Current?.LogException(ErrorCodes.ClientSettingsImportExportFailure, "settings export failed", ex);
            }
        }

        private void ImportSettings()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Import RadioRelay Settings",
                Filter = "RadioRelay settings (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var imported = AppSettings.ImportFromFile(dialog.FileName);
                _settings.CopyFrom(imported);
                ApplyImportedSettingsToUi();
                _settings.Save();
                LogSafe($"Settings imported from {dialog.FileName}");
            }
            catch (Exception ex)
            {
                LogSafe($"Settings import failed: {ex.Message}");
                ClientDiagnostics.Current?.LogException(ErrorCodes.ClientSettingsImportExportFailure, "settings import failed", ex);
            }
        }

        private void ApplyImportedSettingsToUi()
        {
            ApplySavedRadioSettingsToExistingChannels();
            ApplyChannelSettingsToRows();
            ApplySavedGlobalSettings();
            ApplySavedPttBindings();
            ResubscribeIfConnected();
        }

        private void ApplyChannelSettingsToRows()
        {
            foreach (var row in _radioRows)
            {
                row.NameBox.Text = row.Channel.DisplayName;
                row.Freq.Value = (decimal)Math.Clamp(row.Channel.Frequency, (float)row.Freq.Minimum, (float)row.Freq.Maximum);
                row.Vol.Value = Math.Clamp((int)Math.Round(row.Channel.Volume * 100), row.Vol.Minimum, row.Vol.Maximum);
                row.VolumeValueLabel.Text = $"{row.Vol.Value}%";
                row.Ear.SelectedItem = row.Channel.Ear;
                row.Passcode.Text = row.Channel.Passcode;
                UpdateEncryptState(row);
                row.ColorButton.BackColor = row.Channel.HudColor;
                _overlay.SetUserCount(row.Channel, 0);
                if (row.Channel.Volume <= 0f) _overlay.SuppressChannel(row.Channel);
                UpdateRadioActivity(row.Channel);
            }
        }

        private static PttSlotSettings ToSlotSettings(PttBinding? binding) => new()
        {
            Type = binding?.Type,
            KeyCode = binding?.KeyCode ?? 0,
            DeviceGuid = binding?.DeviceGuid ?? Guid.Empty,
            ButtonIndex = binding?.ButtonIndex ?? 0,
            DisplayName = binding?.DisplayName ?? ""
        };

        private void OnPttDown(RadioChannel channel)
        {
            if (!RadioReceiveMute.CanStartTransmission(channel.Volume)) return;

            if (_pttReleaseTimers.TryGetValue(channel, out var existing))
            {
                existing.Dispose();
                _pttReleaseTimers.Remove(channel);
            }
            _audioEngine?.SetTransmitting(channel, true);
        }

        private void CancelPttReleaseTimer(RadioChannel channel)
        {
            if (!_pttReleaseTimers.TryGetValue(channel, out var timer)) return;
            timer.Dispose();
            _pttReleaseTimers.Remove(channel);
        }

        private void OnPttUp(RadioChannel channel)
        {
            if (!RadioReceiveMute.CanStartTransmission(channel.Volume))
            {
                CancelPttReleaseTimer(channel);
                _audioEngine?.SetTransmitting(channel, false);
                return;
            }

            if (_pttReleaseDelayMs <= 0)
            {
                _audioEngine?.SetTransmitting(channel, false);
                return;
            }
            // Lets the mic keep capturing for a short "tail" after physical
            // release so a quick re-press doesn't clip the end of a word,
            // and a real release still ends the transmission after the delay.
            if (_pttReleaseTimers.TryGetValue(channel, out var existing))
                existing.Dispose();

            _pttReleaseTimers[channel] = new System.Threading.Timer(
                _ => RunBackgroundCallback(() => _audioEngine?.SetTransmitting(channel, false)), null, _pttReleaseDelayMs, Timeout.Infinite);
        }

        private void StartPttCapture(RadioChannel channel, PttSlot slot, Button button, Label label)
        {
            if (_controlLockEnabled)
            {
                LogSafe("Unlock controls before changing PTT bindings.");
                return;
            }

            var originalText = button.Text;
            button.Text = "...";
            button.Enabled = false;

            _pttInput.StartCapture(channel, slot, binding =>
            {
                if (InvokeRequired) { PostToUi(() => ApplyCapturedBinding(channel, binding, button, label, originalText)); return; }
                ApplyCapturedBinding(channel, binding, button, label, originalText);
            });
        }

        private void ApplyCapturedBinding(
            RadioChannel channel,
            PttBinding? binding,
            Button button,
            Label label,
            string originalButtonText)
        {
            button.Text = originalButtonText;
            button.Enabled = true;

            if (binding == null)
            {
                label.Text = "Unbound";
                label.ForeColor = Theme.AccentOrange;
                LogSafe($"{channel.Name} PTT binding cleared.");
                return;
            }

            label.Text = CompactPttDisplayName(binding.DisplayName);
            label.ForeColor = Theme.AccentGreen;
            LogSafe($"{channel.Name} PTT bound to {binding.DisplayName}");
        }

        private PresenceSubscription[] BuildPresenceSubscriptions() =>
            _channels.ConvertAll(c => new PresenceSubscription
            {
                Frequency = c.Frequency,
                NetIdHash = c.SelectedNet.NetIdHash
            }).ToArray();

        private void ResubscribeIfConnected()
        {
            if (_relayClient?.IsConnected != true) return;

            try
            {
                _relayClient.Callsign = _callsignBox.Text;
                _relayClient.ServerPassword = _serverPasswordBox.Text;
                _relayClient.SendSubscribe(BuildPresenceSubscriptions());
            }
            catch (Exception ex)
            {
                LogSafe($"Resubscribe failed: {ex.Message}");
                ClientDiagnostics.Current?.LogException(ErrorCodes.ClientConnectFailure, "resubscribe failed from MainForm", ex);
            }
        }

        private void ToggleConnection()
        {
            if (_relayClient == null || !_relayClient.IsConnected)
            {
                _connectionEstablished = false;
                _relayClient = new RelayClient(_identity) { Callsign = _callsignBox.Text };
                _relayClient.StatusChanged += LogSafe;
                _relayClient.AudioReceived += packet => _audioEngine?.OnAudioReceived(packet);
                _relayClient.PresenceUpdated += OnPresenceUpdated;
                _relayClient.ConnectionHealthChanged += OnConnectionHealthChanged;

                try
                {
                    _relayClient.Connect(_serverBox.Text, (int)_portBox.Value, _serverPasswordBox.Text);
                    _relayClient.SendSubscribe(BuildPresenceSubscriptions());

                    _statusLabel.Text = "Connecting...";
                    _statusLabel.ForeColor = Theme.AccentOrange;
                    _connectButton.Text = "Disconnect";
                }
                catch (Exception ex)
                {
                    LogSafe($"Connect failed: {ex.Message}");
                    ClientDiagnostics.Current?.LogException(ErrorCodes.ClientConnectFailure, "connect failed from MainForm", ex);
                }
            }
            else
            {
                bool hadEstablishedConnection = _connectionEstablished;
                _connectionEstablished = false;
                _relayClient.Disconnect();
                _statusLabel.Text = "Disconnected";
                _statusLabel.ForeColor = Theme.AccentRed;
                _connectButton.Text = "Connect";
                OnPresenceUpdated(Array.Empty<PresenceChannelCount>());
                if (hadEstablishedConnection) _audioEngine?.PlayDisconnectedBeep();
            }
        }

        private void OnPresenceUpdated(PresenceChannelCount[] counts)
        {
            if (InvokeRequired) { PostToUi(() => OnPresenceUpdated(counts)); return; }

            foreach (var row in _radioRows)
            {
                int count = PresenceDisplay.CountFor(row.Channel, counts);
                row.UserCountLabel.Text = PresenceDisplay.FormatCount(count);
                row.UserCountLabel.ForeColor = count > 0 ? Theme.AccentGreen : Theme.MutedText;
                _overlay.SetUserCount(row.Channel, count);
            }
        }

        private void OnConnectionHealthChanged(bool healthy)
        {
            if (InvokeRequired) { PostToUi(() => OnConnectionHealthChanged(healthy)); return; }

            if (healthy)
            {
                bool wasEstablished = _connectionEstablished;
                _connectionEstablished = true;
                _statusLabel.Text = "Connected";
                _statusLabel.ForeColor = Theme.AccentGreen;
                if (!wasEstablished)
                {
                    _audioEngine?.PlayConnectedBeep();
                    ResubscribeIfConnected();
                }
                LogSafe(wasEstablished ? "Connection restored." : "Connection established.");
            }
            else
            {
                bool hadEstablishedConnection = _connectionEstablished;
                _connectionEstablished = false;
                if (hadEstablishedConnection) _audioEngine?.PlayDisconnectedBeep();
                _statusLabel.Text = "Error";
                _statusLabel.ForeColor = Theme.AccentRed;
                LogSafe("No response from server.");
            }
        }

        private void LogSafe(string message)
        {
            if (InvokeRequired) { PostToUi(() => LogSafe(message)); return; }
            ClientDiagnostics.Current?.LogLifecycle(ErrorCodes.ClientFormShown, message);
            _logBox.Items.Add($"{DateTime.Now:T}  {message}");
            if (_logBox.Items.Count > 0) _logBox.TopIndex = _logBox.Items.Count - 1;
        }

        private void PostToUi(Action action)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static void RunBackgroundCallback(Action action)
        {
            try { action(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ClientDiagnostics.Current?.LogLifecycle(ErrorCodes.ClientFormClosed, "main form closed");
            _updateCheckCancellation.Cancel();
            _updateCheckCancellation.Dispose();
            SaveCurrentSettings();

            foreach (var timer in _pttReleaseTimers.Values) timer.Dispose();
            _relayClient?.Disconnect();
            _audioEngine?.Dispose();
            _pttInput.Dispose();
            _overlay.Close();
            _overlay.Dispose();
            base.OnFormClosed(e);
        }

        private readonly struct DeviceItem
        {
            public readonly int Index;
            public readonly string Name;
            public DeviceItem(int index, string name) { Index = index; Name = name; }
            public override string ToString() => Name;
        }
    }
}
