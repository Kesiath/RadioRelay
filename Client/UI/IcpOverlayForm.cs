using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RadioRelay.Client.Radio;

namespace RadioRelay.Client.UI
{
    /// <summary>Clickable, always-on-top radio preset controller inspired by an F-16 ICP.</summary>
    public sealed class IcpOverlayForm : Form
    {
        private readonly IReadOnlyList<RadioChannel> _channels;
        private readonly List<ModernButton> _radioButtons = new();
        private readonly List<ModernButton> _channelButtons = new();
        private readonly Label _readout = new();
        private readonly ModernSlider _volumeSlider = new() { Minimum = 0, Maximum = 100, Enabled = false };
        private readonly Label _volumeValue = new();
        private RadioChannel? _selectedRadio;
        private int? _pendingChannel;
        private int? _pendingVolume;
        private int? _initialVolume;
        private bool _editMode;
        private bool _wasVisibleBeforeEdit;
        private Point _dragOffset;
        private bool _dragging;

        public event Action<RadioChannel, int?, int>? ChangeConfirmed;
        public event Action? LayoutChanged;
        public event Action? EscapeRequested;

        public IcpOverlayForm(IReadOnlyList<RadioChannel> channels)
        {
            _channels = channels;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Background;
            ClientSize = new Size(430, 376);
            KeyPreview = true;
            DoubleBuffered = true;
            Opacity = 0.96;
            UpdateRoundedRegion();

            var card = new ModernPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.CardBackground,
                BorderColor = Theme.Border,
                CornerRadius = 12,
                Padding = new Padding(14)
            };
            Controls.Add(card);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                BackColor = Theme.CardBackground,
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 78));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            card.Controls.Add(layout);

            var radioRow = CreateGrid(3, 1, 6);
            for (int i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                var button = CreateButton(channel.DisplayName);
                button.SizeChanged += (_, _) => FitButtonText(button);
                button.Click += (_, _) => SelectRadio(channel);
                _radioButtons.Add(button);
                radioRow.Controls.Add(button, i, 0);
            }
            layout.Controls.Add(radioRow, 0, 0);

            _readout.Dock = DockStyle.Fill;
            _readout.TextAlign = ContentAlignment.MiddleCenter;
            _readout.Font = Theme.MonoFont;
            _readout.ForeColor = Theme.HeaderText;
            _readout.Text = "SELECT RADIO";
            _readout.MouseDown += BeginDrag;
            _readout.MouseMove += ContinueDrag;
            _readout.MouseUp += EndDrag;
            layout.Controls.Add(_readout, 0, 1);

            var keypad = CreateGrid(3, 3, 6);
            for (int number = 1; number <= 9; number++)
            {
                int selected = number;
                var button = CreateButton(number.ToString());
                button.Font = new Font(Theme.MonoFont.FontFamily, 14f, FontStyle.Bold);
                button.Click += (_, _) => SelectChannel(selected);
                _channelButtons.Add(button);
                keypad.Controls.Add(button, (number - 1) % 3, (number - 1) / 3);
            }
            layout.Controls.Add(keypad, 0, 2);

            var actions = CreateGrid(1, 2, 8);
            // The card already supplies equal outer padding. Keep the action
            // column flush with that inner right edge and put its separation
            // from the keypad on the left, avoiding a doubled right gutter.
            actions.Padding = new Padding(0, 0, 0, 8);
            var enter = CreateButton("ENTER");
            enter.Margin = new Padding(6, 0, 0, 6);
            enter.BackColor = Theme.TealDim;
            enter.Click += (_, _) => Confirm();
            var exit = CreateButton("EXIT");
            exit.Margin = new Padding(6, 0, 0, 6);
            exit.BackColor = Theme.AmberDim;
            exit.Click += (_, _) => HideAndReset();
            actions.Controls.Add(enter, 0, 0);
            actions.Controls.Add(exit, 0, 1);
            layout.Controls.Add(actions, 1, 0);
            layout.SetRowSpan(actions, 3);

            var volumeRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Theme.CardBackground,
                Margin = Padding.Empty,
                Padding = new Padding(4, 8, 4, 0)
            };
            volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            var volumeTitle = new Label
            {
                Text = "VOLUME",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = Theme.SmallMonoFont,
                ForeColor = Theme.HeaderText
            };
            _volumeSlider.Dock = DockStyle.Fill;
            _volumeSlider.Margin = new Padding(2, 0, 8, 0);
            _volumeValue.Dock = DockStyle.Fill;
            _volumeValue.Text = "--";
            _volumeValue.TextAlign = ContentAlignment.MiddleRight;
            _volumeValue.Font = Theme.MonoFont;
            _volumeValue.ForeColor = Theme.MutedText;
            _volumeSlider.ValueChanged += (_, _) =>
            {
                if (_selectedRadio == null) return;
                _pendingVolume = _volumeSlider.Value;
                _volumeValue.Text = $"{_volumeSlider.Value}%";
                _volumeValue.ForeColor = Theme.Text;
                UpdateVisualState();
            };
            volumeRow.Controls.Add(volumeTitle, 0, 0);
            volumeRow.Controls.Add(_volumeSlider, 1, 0);
            volumeRow.Controls.Add(_volumeValue, 2, 0);
            layout.Controls.Add(volumeRow, 0, 3);
            layout.SetColumnSpan(volumeRow, 2);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    if (_editMode) EscapeRequested?.Invoke();
                    else HideAndReset();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Enter) Confirm();
            };
            Deactivate += (_, _) => TopMost = true;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRoundedRegion();
        }

        private void UpdateRoundedRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

            using var path = Theme.RoundedRect(ClientRectangle, 12);
            var oldRegion = Region;
            Region = new Region(path);
            oldRegion?.Dispose();
        }

        private static TableLayoutPanel CreateGrid(int columns, int rows, int gap)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                BackColor = Theme.CardBackground,
                Padding = new Padding(0, 0, gap, gap),
                Margin = Padding.Empty
            };
            for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            return grid;
        }

        private static ModernButton CreateButton(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 6),
            BackColor = Theme.RaisedBackground,
            ForeColor = Theme.Text,
            CornerRadius = 7
        };

        public void ToggleOverlay()
        {
            if (Visible) HideAndReset();
            else ShowOverlay();
        }

        public void ShowOverlay()
        {
            RefreshRadioNames();
            EnsureVisibleOnScreen();
            ResetSelection();
            Show();
            BringToFront();
        }

        public void HideAndReset()
        {
            ResetSelection();
            if (!_editMode) Hide();
        }

        public void SetEditMode(bool enabled)
        {
            if (_editMode == enabled) return;
            _editMode = enabled;
            if (enabled)
            {
                _wasVisibleBeforeEdit = Visible;
                ShowOverlay();
                _readout.Text = "DRAG HERE TO POSITION ICP";
                _readout.ForeColor = Theme.AccentBlue;
            }
            else
            {
                ResetSelection();
                if (!_wasVisibleBeforeEdit) Hide();
            }
        }

        public void SetSavedPosition(int? x, int? y)
        {
            if (x.HasValue && y.HasValue) Location = new Point(x.Value, y.Value);
            else
            {
                var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
                Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
            }
            EnsureVisibleOnScreen();
        }

        public void RefreshRadioNames()
        {
            for (int i = 0; i < Math.Min(_radioButtons.Count, _channels.Count); i++)
            {
                _radioButtons[i].Text = _channels[i].DisplayName;
                FitButtonText(_radioButtons[i]);
            }
        }

        private static void FitButtonText(Button button)
        {
            float size = Theme.ButtonFont.Size;
            int availableWidth = Math.Max(1, button.ClientSize.Width - button.Padding.Horizontal - 4);
            while (size > 6.5f)
            {
                using var candidate = new Font(Theme.ButtonFont.FontFamily, size, FontStyle.Bold);
                if (TextRenderer.MeasureText(button.Text, candidate, Size.Empty, TextFormatFlags.NoPadding).Width <= availableWidth)
                    break;
                size -= 0.5f;
            }

            var oldFont = button.Font;
            button.Font = Math.Abs(size - Theme.ButtonFont.Size) < 0.1f
                ? Theme.ButtonFont
                : new Font(Theme.ButtonFont.FontFamily, size, FontStyle.Bold);
            if (!ReferenceEquals(oldFont, Theme.ButtonFont) && !ReferenceEquals(oldFont, button.Font)) oldFont.Dispose();
        }

        private void SelectRadio(RadioChannel channel)
        {
            _selectedRadio = channel;
            _pendingChannel = null;
            _pendingVolume = Math.Clamp((int)Math.Round(channel.Volume * 100f), 0, 100);
            _initialVolume = _pendingVolume;
            _volumeSlider.Enabled = true;
            _volumeSlider.Value = _pendingVolume.Value;
            _volumeValue.Text = $"{_pendingVolume.Value}%";
            _volumeValue.ForeColor = Theme.Text;
            UpdateVisualState();
        }

        private void SelectChannel(int channel)
        {
            if (_selectedRadio == null)
            {
                _readout.Text = "SELECT RADIO FIRST";
                _readout.ForeColor = Theme.AccentOrange;
                return;
            }
            _pendingChannel = channel;
            UpdateVisualState();
        }

        private void Confirm()
        {
            if (_selectedRadio == null || !_pendingVolume.HasValue)
            {
                _readout.Text = "SELECT RADIO";
                _readout.ForeColor = Theme.AccentOrange;
                return;
            }
            if (!_pendingChannel.HasValue && _pendingVolume == _initialVolume)
            {
                _readout.Text = "SELECT CHANNEL OR CHANGE VOLUME";
                _readout.ForeColor = Theme.AccentOrange;
                return;
            }
            var radio = _selectedRadio;
            int? channel = _pendingChannel;
            int volume = _pendingVolume.Value;
            ChangeConfirmed?.Invoke(radio, channel, volume);
            _readout.Text = channel.HasValue
                ? $"{radio.DisplayName}  CH {channel.Value}  VOL {volume}%  SET"
                : $"{radio.DisplayName}  VOL {volume}%  SET";
            _readout.ForeColor = Theme.AccentGreen;
            _selectedRadio = null;
            _pendingChannel = null;
            _pendingVolume = null;
            _initialVolume = null;
            _volumeSlider.Enabled = false;
            UpdateButtonColors();
        }

        private void ResetSelection()
        {
            _selectedRadio = null;
            _pendingChannel = null;
            _pendingVolume = null;
            _initialVolume = null;
            _volumeSlider.Enabled = false;
            _volumeValue.Text = "--";
            _volumeValue.ForeColor = Theme.MutedText;
            _readout.Text = _editMode ? "DRAG HERE TO POSITION ICP" : "SELECT RADIO";
            _readout.ForeColor = _editMode ? Theme.AccentBlue : Theme.HeaderText;
            UpdateButtonColors();
        }

        private void UpdateVisualState()
        {
            _readout.Text = _selectedRadio == null
                ? "SELECT RADIO"
                : _pendingChannel.HasValue
                    ? $"{_selectedRadio.DisplayName}  >  CH {_pendingChannel.Value}  VOL {_pendingVolume ?? 0}%"
                    : $"{_selectedRadio.DisplayName}  >  SELECT CHANNEL  VOL {_pendingVolume ?? 0}%";
            _readout.ForeColor = Theme.Text;
            UpdateButtonColors();
        }

        private void UpdateButtonColors()
        {
            for (int i = 0; i < _radioButtons.Count; i++)
                _radioButtons[i].BackColor = ReferenceEquals(_selectedRadio, _channels[i]) ? Theme.TealDim : Theme.RaisedBackground;
            for (int i = 0; i < _channelButtons.Count; i++)
                _channelButtons[i].BackColor = _pendingChannel == i + 1 ? Theme.TealDim : Theme.RaisedBackground;
        }

        private void BeginDrag(object? sender, MouseEventArgs e)
        {
            if (!_editMode || e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOffset = e.Location;
        }

        private void ContinueDrag(object? sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var cursor = Cursor.Position;
            Location = new Point(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y);
        }

        private void EndDrag(object? sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            EnsureVisibleOnScreen();
            LayoutChanged?.Invoke();
        }

        private void EnsureVisibleOnScreen()
        {
            var area = Screen.FromPoint(Location).WorkingArea;
            Location = new Point(
                Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
    }
}
