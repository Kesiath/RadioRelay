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
    public class MainForm : Form
    {
        private class RadioRow
        {
            public required RadioChannel Channel;
            public required NumericUpDown Freq;
            public required TrackBar Vol;
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
            public bool ShowBorder { get; set; } = true;

            public DarkInputHost()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                // The border is painted as a solid fill (whole host filled with
                // the border color, then everything but a 1px margin re-filled
                // with the field color) instead of a stroked outline. A 1px
                // Pen stroke drawn exactly on the last row/column of pixels is
                // exactly the kind of thing TableLayoutPanel/DPI rounding can
                // clip off one edge -- which is what was erasing the right-hand
                // border on the MHz/Key/PTT ms/Port/Pass fields. A fill-based
                // border always leaves a visible ring on every side since
                // there's no thin line that can disappear entirely.
                BackColor = Theme.Border;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using var fieldBrush = new SolidBrush(Theme.FieldBackground);
                if (!ShowBorder)
                {
                    e.Graphics.FillRectangle(fieldBrush, ClientRectangle);
                    return;
                }

                var inner = new Rectangle(1, 1, Math.Max(0, Width - 2), Math.Max(0, Height - 2));
                e.Graphics.FillRectangle(fieldBrush, inner);
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                Invalidate();
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

        private readonly TextBox _serverBox = new() { Text = "127.0.0.1" };
        private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 5060 };
        private readonly TextBox _serverPasswordBox = new() { Text = "", Width = 120, UseSystemPasswordChar = true };
        private readonly Button _connectButton = new() { Text = "Connect" };
        private readonly TextBox _callsignBox = new() { Text = "", MaxLength = 20 };
        private readonly Label _statusLabel = new() { Text = "Disconnected", AutoSize = true, ForeColor = Theme.AccentRed };
        private readonly Label _versionLabel = new() { Text = ApplicationVersion.DisplayName, AutoSize = true, ForeColor = Theme.MutedText };
        private readonly Label _wordmarkLabel = new() { Text = "●  RADIORELAY", AutoSize = true, ForeColor = Theme.MutedText, Font = Theme.TitleFont };

        private readonly NumericUpDown _pttReleaseDelayBox = new() { Minimum = 0, Maximum = 2000, Value = 200, Width = 70 };

        private readonly ComboBox _inputDeviceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _outputDeviceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TrackBar _inputGainSlider = new() { Minimum = 0, Maximum = 300, Value = 100 };
        private readonly TrackBar _inputClickVolSlider = new() { Minimum = 0, Maximum = 100, Value = 100 };
        private readonly TrackBar _talkOverVolSlider = new() { Minimum = 0, Maximum = 100, Value = 100 };
        private readonly TrackBar _outputClickVolSlider = new() { Minimum = 0, Maximum = 100, Value = 100 };

        private readonly Button _hudLayoutButton = new() { Text = "Customize HUD" };
        private readonly Button _controlLockButton = new() { Text = "Lock Controls" };
        private readonly Button _exportSettingsButton = new() { Text = "Export Settings" };
        private readonly Button _importSettingsButton = new() { Text = "Import Settings" };

        private readonly ListBox _logBox = new() { IntegralHeight = false };
        private readonly TableLayoutPanel _page = new() { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly RadioActivityTracker _activityTracker = new();

        private readonly List<RadioRow> _radioRows = new();

        // Sizes for the redesigned compact layout. These intentionally live
        // here rather than in MainFormLayoutPolicy (which still defines
        // MinimumWindowWidth / ContentWidthFor / HorizontalMargin used
        // below) since that policy's old RadioCardHeight / ConnectionStripHeight /
        // SetupStripHeight / OperationsStripHeight / LogHeight / EstimatedMainPageHeight
        // constants described the previous, taller layout and are no longer
        // read anywhere in this file.
        private const int PreferredWindowWidth = 700;
        private const int PreferredContentWidth = 640;
        private const int MinContentWidth = 620;
        private const int CardPadding = 12;
        private const int Gap = 8;
        private const int SafeRightMargin = 10;
        private const int RailWidth = 4;
        private const int CompactRadioCardHeight = 150;    // Header key/mode/HUD row + controls + compact PTT row.
        private const int TopCardHeight = 192;             // Server/device rows + two slider rows for narrower windows.
        private const int ToolbarCardHeight = 60;          // One snug row: PTT ms + four action buttons.
        private const int LogCardHeight = 150;             // 16 padding + 18 title + ~116 list.

        public MainForm()
        {
            _settings = AppSettings.Load();
            _channels = BuildChannelsFromSettings();
            _overlay = new TransmissionOverlayForm(_channels);

            Text = ApplicationVersion.DisplayName;
            Width = PreferredWindowWidth;
            Height = 860;
            MinimumSize = new Size(MinContentWidth + (MainFormLayoutPolicy.HorizontalMargin * 2) + 28, 640);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScroll = true;
            BackColor = Theme.Background;
            Font = Theme.BodyFont;

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
            var card = new Panel
            {
                Width = _page.Width,
                Height = CompactRadioCardHeight,
                BackColor = Theme.CardBackground,
                Padding = new Padding(CardPadding, 8, CardPadding + SafeRightMargin, 8)
            };
            card.Paint += PaintBorder;

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
                BackColor = Theme.CardBackground,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            card.Controls.Add(body);
            body.BringToFront();

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

            statusBadge = CreateBadge("IDLE", Theme.FaintText, Theme.Border);
            return card;
        }

        private static Label CreateBadge(string text, Color foreColor, Color borderColor)
        {
            var badge = new Label
            {
                Text = text,
                AutoSize = false,
                Width = MainFormLayoutPolicy.RadioActivityBadgeWidth,
                Height = 20,
                ForeColor = foreColor,
                Font = Theme.SmallMonoFont,
                Padding = new Padding(6, 2, 6, 2),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0)
            };
            badge.Paint += (_, e) =>
            {
                using var pen = new Pen(borderColor);
                e.Graphics.DrawRectangle(pen, 0, 0, badge.Width - 1, badge.Height - 1);
            };
            return badge;
        }

        private static Label CreateFieldLabel(string text) => new()
        {
            Text = text.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = Theme.FaintText,
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
            c.Margin = new Padding(0);
            c.Dock = DockStyle.None;
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            if (c is TextBox textBox)
            {
                // Draw text inside a custom dark host instead of relying on
                // the native white WinForms border, which was clipping on the
                // right edge at this compact size.
                textBox.BorderStyle = BorderStyle.None;
                textBox.Multiline = false;
            }
            else if (c is NumericUpDown numeric)
            {
                // Make NumericUpDown look like a normal typable dark field.
                // The native spinner strip was the visible white clipped edge
                // on MHz/PTT MS/Port, so hide it and keep direct typing plus
                // the existing ValueChanged behavior.
                numeric.BorderStyle = BorderStyle.None;
                numeric.TextAlign = HorizontalAlignment.Left;
                HideNumericSpinner(numeric);
                numeric.HandleCreated += (_, _) => HideNumericSpinner(numeric);
                numeric.Resize += (_, _) => HideNumericSpinner(numeric);
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

        private static void HideNumericSpinner(NumericUpDown numeric)
        {
            foreach (Control child in numeric.Controls)
            {
                var typeName = child.GetType().Name;
                if (typeName.Contains("Buttons", StringComparison.OrdinalIgnoreCase))
                {
                    child.Visible = false;
                    child.Width = 0;
                    continue;
                }

                // Stretch the edit portion over the area the spinner used to
                // occupy so the field reads as one clean, typable text box.
                child.Left = 0;
                child.Top = 0;
                child.Width = numeric.ClientSize.Width;
                child.Height = numeric.ClientSize.Height;
                child.BackColor = Theme.FieldBackground;
                child.ForeColor = Theme.Text;
            }
        }

        private static Panel CreateCompactInputHost(Control control)
        {
            StyleCompactInput(control);

            var host = new DarkInputHost
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.FieldBackground,
                Margin = new Padding(0),
                ShowBorder = control is not (ComboBox or DarkComboBox),
                Padding = control is ComboBox or DarkComboBox ? new Padding(5, 3, 6, 3) : new Padding(6, 4, 6, 3)
            };

            void LayoutChild()
            {
                LayoutCompactInputChild(host, control);
            }

            host.Controls.Add(control);
            host.Layout += (_, _) => LayoutChild();
            host.Resize += (_, _) => LayoutChild();
            control.Resize += (_, _) => host.Invalidate();
            LayoutChild();
            return host;
        }

        private static void LayoutCompactInputChild(Control host, Control control)
        {
            int left = host.Padding.Left;
            int top = host.Padding.Top;
            int width = Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal);
            int height = Math.Max(1, host.ClientSize.Height - host.Padding.Vertical);

            if (control is ComboBox or DarkComboBox)
            {
                // ComboBox ignores some height requests. Keep it vertically
                // centered and safely inset so its native painting never
                // touches the custom host border.
                int comboHeight = Math.Min(Math.Max(control.Height, 21), height);
                int comboTop = top + Math.Max(0, (height - comboHeight) / 2);
                control.SetBounds(left, comboTop, width, comboHeight);
            }
            else
            {
                control.SetBounds(left, top, width, height);
            }

            if (control is NumericUpDown numeric)
                HideNumericSpinner(numeric);
        }

        private static void StyleSlider(TrackBar slider)
        {
            slider.BackColor = Theme.CardBackground;
            slider.TickStyle = TickStyle.None;
            slider.AutoSize = false;
            slider.Height = 24;
            slider.Margin = new Padding(0);
            slider.SmallChange = 1;
            slider.LargeChange = 5;
        }

        private static TableLayoutPanel CreateSliderCluster(string label, TrackBar slider, Label? valueLabel = null, int width = 150)
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
            b.BackColor = Theme.CardBackground;
            b.ForeColor = Theme.Text;
            b.Font = Theme.SmallMonoFont;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.BorderSize = 1;
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

        private static TableLayoutPanel CreateFixedField(string label, Control control, int width)
        {
            var panel = new TableLayoutPanel
            {
                Width = width,
                Height = 44,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            control.Width = width - 2;
            control.Height = Math.Max(control.Height, 24);
            control.Margin = new Padding(0, 0, 2, 0);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
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
            var panel = new TableLayoutPanel
            {
                Width = width,
                Height = 44,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(CreateCaption(label), 0, 0);
            panel.Controls.Add(CreateCompactInputHost(control), 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateFillField(string label, Control control)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            control.Height = Math.Max(control.Height, 24);
            control.Margin = new Padding(0, 0, 2, 0);
            control.Dock = DockStyle.Fill;
            if (control is Label valueLabel)
            {
                valueLabel.AutoSize = false;
                valueLabel.TextAlign = ContentAlignment.MiddleLeft;
            }
            panel.Controls.Add(CreateCaption(label), 0, 0);
            panel.Controls.Add(control, 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateSliderCell(string label, TrackBar slider, Label? valueLabel = null)
        {
            StyleSlider(slider);
            slider.Margin = new Padding(0);
            slider.Dock = DockStyle.Fill;

            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = valueLabel == null ? 1 : 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            if (valueLabel != null)
            {
                header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            }

            header.Controls.Add(CreateCaption(label), 0, 0);
            if (valueLabel != null)
            {
                valueLabel.AutoSize = false;
                valueLabel.Dock = DockStyle.Fill;
                valueLabel.Font = Theme.SmallMonoFont;
                valueLabel.ForeColor = Theme.MutedText;
                valueLabel.TextAlign = ContentAlignment.MiddleRight;
                valueLabel.Margin = new Padding(0);
                header.Controls.Add(valueLabel, 1, 0);
            }

            var sliderHost = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(10, 5, 10, 0),
                BackColor = Theme.CardBackground
            };
            sliderHost.Controls.Add(slider);

            panel.Controls.Add(header, 0, 0);
            panel.Controls.Add(sliderHost, 0, 1);
            return panel;
        }

        private static TableLayoutPanel CreateServerRow(
            TextBox serverBox,
            NumericUpDown portBox,
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
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            connectButton.Width = 96;
            connectButton.Height = 26;
            connectButton.Margin = new Padding(0, 16, 0, 2);
            connectButton.Dock = DockStyle.Fill;

            statusLabel.AutoSize = false;
            statusLabel.Font = Theme.SmallMonoFont;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.AutoEllipsis = true;
            statusLabel.Margin = new Padding(0, 16, 0, 2);
            statusLabel.Dock = DockStyle.Fill;

            versionLabel.AutoSize = false;
            versionLabel.Font = Theme.SmallMonoFont;
            versionLabel.TextAlign = ContentAlignment.MiddleRight;
            versionLabel.AutoEllipsis = true;
            versionLabel.Margin = new Padding(Gap, 16, 0, 2);
            versionLabel.Dock = DockStyle.Fill;

            row.Controls.Add(CreateCompactField("Server", serverBox, 112), 0, 0);
            row.Controls.Add(CreateCompactField("Port", portBox, 64), 2, 0);
            row.Controls.Add(CreateCompactField("Pass", passwordBox, 86), 4, 0);
            row.Controls.Add(connectButton, 6, 0);
            row.Controls.Add(statusLabel, 8, 0);
            row.Controls.Add(versionLabel, 9, 0);
            return row;
        }

        private static TableLayoutPanel CreateDeviceRow(TextBox callsignBox, ComboBox inputDeviceBox, ComboBox outputDeviceBox)
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
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            row.Controls.Add(CreateCompactField("Name", callsignBox, 120), 0, 0);
            row.Controls.Add(CreateFillField("Input", inputDeviceBox), 2, 0);
            row.Controls.Add(CreateFillField("Output", outputDeviceBox), 4, 0);
            return row;
        }

        private static TableLayoutPanel CreateTopSliderRow(
            TrackBar inputGain,
            TrackBar txClick,
            TrackBar rxClick,
            TrackBar talkover)
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
            TrackBar leftSlider,
            TrackBar rightSlider,
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
            NumericUpDown pttReleaseDelayBox,
            Button controlLockButton,
            Button hudLayoutButton,
            Button exportSettingsButton,
            Button importSettingsButton)
        {
            const int buttonHeight = 26;
            controlLockButton.Width = 116;
            hudLayoutButton.Width = 116;
            exportSettingsButton.Width = 108;
            importSettingsButton.Width = 108;
            foreach (var button in new[] { controlLockButton, hudLayoutButton, exportSettingsButton, importSettingsButton })
            {
                button.Height = buttonHeight;
                button.Dock = DockStyle.Fill;
                button.Margin = new Padding(0, 16, 0, 2);
            }

            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 10,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, controlLockButton.Width));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, hudLayoutButton.Width));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, exportSettingsButton.Width));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, importSettingsButton.Width));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            row.Controls.Add(CreateCompactField("PTT ms", pttReleaseDelayBox, 86), 0, 0);
            row.Controls.Add(controlLockButton, 2, 0);
            row.Controls.Add(hudLayoutButton, 4, 0);
            row.Controls.Add(exportSettingsButton, 6, 0);
            row.Controls.Add(importSettingsButton, 8, 0);
            return row;
        }

        private static TableLayoutPanel CreateRadioHeaderRow(
            RadioChannel channel,
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
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MainFormLayoutPolicy.RadioActivityBadgeColumnWidth));

            var title = new Label
            {
                Text = channel.Name,
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Theme.MutedText,
                Font = Theme.RadioTitleFont,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(2, 0, 0, 0)
            };

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
            statusBadge.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            statusBadge.Margin = new Padding(0, 4, 0, 0);

            row.Controls.Add(title, 0, 0);
            row.Controls.Add(CreateFixedField("Users", userCountLabel, 76), 2, 0);
            row.Controls.Add(CreateCompactField("Key", passcode, 108), 4, 0);
            row.Controls.Add(CreateFixedField("Mode", encryptState, 76), 6, 0);
            row.Controls.Add(CreateFixedField("HUD", colorButton, 50), 8, 0);
            row.Controls.Add(statusBadge, 10, 0);
            return row;
        }

        private static TableLayoutPanel CreateRadioSettingsRow(
            NumericUpDown freq,
            TrackBar vol,
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
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            // Intentional empty right spacer: keeps EAR from crowding the
            // status rail/context on the far right while preserving a wide
            // volume slider in the middle.
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));

            row.Controls.Add(CreateCompactField("MHz", freq, 90), 0, 0);
            row.Controls.Add(CreateSliderCell("VOL", vol, volValue), 2, 0);
            row.Controls.Add(CreateCompactField("Ear", ear, 88), 4, 0);
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
                        TrackBar => new Padding(0, 0, 14, 0),
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
            const int pttRowHeight = 28;
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Gap + 8));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, pttRowHeight));

            pttPrimaryButton.Text = "PTT A";
            pttSecondaryButton.Text = "PTT B";
            foreach (var button in new[] { pttPrimaryButton, pttSecondaryButton })
            {
                button.Dock = DockStyle.Fill;
                button.Height = 24;
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

        private int CurrentContentWidth()
        {
            var available = MainFormLayoutPolicy.ContentWidthFor(ClientSize.Width);
            return Math.Max(MinContentWidth, Math.Min(PreferredContentWidth, available));
        }

        private void BuildUi()
        {
            _page.RowCount = 0;
            _page.RowStyles.Clear();
            _page.Controls.Clear();
            _radioRows.Clear();

            _page.Width = CurrentContentWidth();
            _page.Left = Math.Max(MainFormLayoutPolicy.HorizontalMargin, (ClientSize.Width - _page.Width) / 2);
            _page.Top = MainFormLayoutPolicy.HorizontalMargin + 26;
            _page.BackColor = Theme.Background;
            _page.Margin = new Padding(0);
            Controls.Add(_page);

            _wordmarkLabel.Left = _page.Left;
            _wordmarkLabel.Top = 7;
            Controls.Add(_wordmarkLabel);

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

            foreach (var (index, name) in AudioDeviceEnumerator.GetInputDevices()) _inputDeviceBox.Items.Add(new DeviceItem(index, name));
            foreach (var (index, name) in AudioDeviceEnumerator.GetOutputDevices()) _outputDeviceBox.Items.Add(new DeviceItem(index, name));

            // ---- Server + identity + audio ----
            var topCard = new TableLayoutPanel
            {
                Width = _page.Width,
                Height = TopCardHeight,
                BackColor = Theme.CardBackground,
                Padding = new Padding(CardPadding, 8, CardPadding + SafeRightMargin, 8),
                ColumnCount = 1,
                RowCount = 4,
                Margin = new Padding(0)
            };
            topCard.Paint += PaintBorder;
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            topCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            topCard.Controls.Add(CreateServerRow(_serverBox, _portBox, _serverPasswordBox, _connectButton, _statusLabel, _versionLabel), 0, 0);
            topCard.Controls.Add(CreateDeviceRow(_callsignBox, _inputDeviceBox, _outputDeviceBox), 0, 1);
            topCard.Controls.Add(CreateTopSliderPairRow(_inputGainSlider, _inputClickVolSlider, "Input gain", "TX click"), 0, 2);
            topCard.Controls.Add(CreateTopSliderPairRow(_outputClickVolSlider, _talkOverVolSlider, "RX click", "Talkover"), 0, 3);
            AddPageRow(topCard, 8);

            // ---- PTT release delay + HUD/lock/export/import ----
            var toolbar = new TableLayoutPanel
            {
                Width = _page.Width,
                Height = ToolbarCardHeight,
                BackColor = Theme.CardBackground,
                Padding = new Padding(CardPadding, 8, CardPadding + SafeRightMargin, 8),
                ColumnCount = 1,
                RowCount = 1,
                Margin = new Padding(0)
            };
            toolbar.Paint += PaintBorder;
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.Controls.Add(CreateToolbarRows(_pttReleaseDelayBox, _controlLockButton, _hudLayoutButton, _exportSettingsButton, _importSettingsButton), 0, 0);
            AddPageRow(toolbar, 8);

            foreach (var ch in _channels)
            {
                var card = CreateRadioCard(ch, out var body, out var statusBadge, out var rail, out var userCountHeaderLabel);

                var freq = new NumericUpDown
                {
                    Width = 84,
                    DecimalPlaces = 3,
                    Increment = 0.025m,
                    Minimum = 2,
                    Maximum = 999,
                    Value = (decimal)Math.Clamp(ch.Frequency, 2f, 999f)
                };
                StyleField(freq);

                var vol = new TrackBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = Math.Clamp((int)Math.Round(ch.Volume * 100), 0, 100),
                    BackColor = Theme.CardBackground
                };
                var volValue = CreateLabel($"{vol.Value}%", muted: true);
                volValue.Font = Theme.SmallMonoFont;
                StyleSlider(vol);

                var ear = new DarkComboBox { Width = 82, DropDownWidth = 96 };
                StyleField(ear);
                ear.Items.AddRange(new object[] { RadioEar.Left, RadioEar.Both, RadioEar.Right });
                ear.SelectedItem = ch.Ear;

                var passcode = new TextBox { Width = 94, Text = ch.Passcode, UseSystemPasswordChar = true };
                StyleField(passcode);

                var encryptStateVisual = RadioEncryptionVisualState.ForPasscode(ch.Passcode);
                var encryptState = CreateLabel(encryptStateVisual.Text);
                encryptState.ForeColor = encryptStateVisual.ForeColor;
                encryptState.Font = Theme.SmallMonoFont;
                var userCountLabel = userCountHeaderLabel;

                var colorButton = new Button
                {
                    Text = "",
                    Width = 40,
                    Height = 24,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = ch.HudColor,
                    ForeColor = Theme.Text
                };
                colorButton.FlatAppearance.BorderColor = Theme.Border;
                colorButton.FlatAppearance.BorderSize = 1;

                body.Controls.Add(CreateRadioHeaderRow(ch, userCountHeaderLabel, passcode, encryptState, colorButton, statusBadge), 0, 0);
                body.Controls.Add(CreateRadioSettingsRow(freq, vol, volValue, ear), 0, 1);

                var pttPrimaryButton = new Button { Text = "PTT A", Width = 68, Height = 24 };
                StyleButton(pttPrimaryButton);
                var pttPrimaryLabel = CreateLabel("Unbound", muted: true);
                pttPrimaryLabel.AutoSize = false;

                var pttSecondaryButton = new Button { Text = "PTT B", Width = 68, Height = 24 };
                StyleButton(pttSecondaryButton);
                var pttSecondaryLabel = CreateLabel("Unbound", muted: true);
                pttSecondaryLabel.AutoSize = false;

                body.Controls.Add(CreatePttRow(pttPrimaryLabel, pttPrimaryButton, pttSecondaryLabel, pttSecondaryButton), 0, 2);

                _radioRows.Add(new RadioRow
                {
                    Channel = ch,
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
                AddPageRow(card, 8);
            }

            // ---- Log ----
            var logCard = new TableLayoutPanel
            {
                Width = _page.Width,
                Height = LogCardHeight,
                BackColor = Theme.CardBackground,
                Padding = new Padding(CardPadding, 8, CardPadding + SafeRightMargin, 8),
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            logCard.Paint += PaintBorder;
            logCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            logCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logCard.Controls.Add(CreateCaption("Log"), 0, 0);

            _logBox.Dock = DockStyle.Fill;
            _logBox.Margin = new Padding(0, 4, 0, 0);
            _logBox.BorderStyle = BorderStyle.FixedSingle;
            StyleField(_logBox);
            logCard.Controls.Add(_logBox, 0, 1);
            AddPageRow(logCard, MainFormLayoutPolicy.HorizontalMargin);

            UpdateAutoScrollMinSize();
            Resize += (_, _) => RelayoutPage();
        }

        private void RelayoutPage()
        {
            var width = CurrentContentWidth();
            _page.Width = width;
            _page.Left = Math.Max(MainFormLayoutPolicy.HorizontalMargin, (ClientSize.Width - width) / 2);
            _wordmarkLabel.Left = _page.Left;
            foreach (Control child in _page.Controls) child.Width = width;
            _page.PerformLayout();
            UpdateAutoScrollMinSize();
            Invalidate(true);
        }

        private void UpdateAutoScrollMinSize()
        {
            _page.PerformLayout();
            AutoScrollMinSize = new Size(
                0,
                _page.Top + _page.PreferredSize.Height + MainFormLayoutPolicy.HorizontalMargin);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ClientDiagnostics.Current?.LogLifecycle(ErrorCodes.ClientFormShown, "main form shown");
            RelayoutPage();

            Refresh();
        }

        private void WireEvents()
        {
            foreach (var row in _radioRows)
            {
                var localRow = row;

                localRow.Freq.ValueChanged += (_, _) =>
                {
                    localRow.Channel.Frequency = (float)localRow.Freq.Value;
                    ResubscribeIfConnected();
                };
                localRow.Vol.ValueChanged += (_, _) =>
                {
                    localRow.Channel.Volume = localRow.Vol.Value / 100f;
                    localRow.VolumeValueLabel.Text = $"{localRow.Vol.Value}%";
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
            var activity = _activityTracker.GetActivity(channel);
            if (activity == RadioActivityKind.Transmitting)
            {
                row.StatusBadge.Text = "TX";
                row.StatusBadge.ForeColor = Theme.AccentOrange;
                row.Rail.BackColor = Theme.AccentOrange;
            }
            else if (activity == RadioActivityKind.Receiving)
            {
                row.StatusBadge.Text = "RX";
                row.StatusBadge.ForeColor = Theme.AccentGreen;
                row.Rail.BackColor = Theme.AccentGreen;
            }
            else
            {
                row.StatusBadge.Text = "IDLE";
                row.StatusBadge.ForeColor = Theme.FaintText;
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
                row.Freq.Enabled = state.CanEditFrequency;
                row.Passcode.Enabled = state.CanEditPasscode;
                row.PttPrimaryButton.Enabled = state.CanChangePttBinding;
                row.PttSecondaryButton.Enabled = state.CanChangePttBinding;
            }
        }

        private static void SelectDeviceItem(ComboBox box, int deviceIndex)
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
            using var dialog = new FolderBrowserDialog
            {
                Description = "Export Folder"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var path = _settings.ExportToDirectory(dialog.SelectedPath);
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
                Filter = "RadioRelay settings (*.json)|*.json|All files (*.*)|*.*",
                FileName = AppSettings.ExportFileName
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
                row.Freq.Value = (decimal)Math.Clamp(row.Channel.Frequency, (float)row.Freq.Minimum, (float)row.Freq.Maximum);
                row.Vol.Value = Math.Clamp((int)Math.Round(row.Channel.Volume * 100), row.Vol.Minimum, row.Vol.Maximum);
                row.VolumeValueLabel.Text = $"{row.Vol.Value}%";
                row.Ear.SelectedItem = row.Channel.Ear;
                row.Passcode.Text = row.Channel.Passcode;
                UpdateEncryptState(row);
                row.ColorButton.BackColor = row.Channel.HudColor;
                _overlay.SetUserCount(row.Channel, 0);
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
            if (_pttReleaseTimers.TryGetValue(channel, out var existing))
            {
                existing.Dispose();
                _pttReleaseTimers.Remove(channel);
            }
            _audioEngine?.SetTransmitting(channel, true);
        }

        private void OnPttUp(RadioChannel channel)
        {
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