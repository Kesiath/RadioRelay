using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>
    /// A plain text editor with numeric validation and a NumericUpDown-like
    /// value API, but no native spinner/button strip. This keeps the compact
    /// rounded field appearance consistent while preserving bounds checking
    /// and ValueChanged notifications used by the radio client.
    /// </summary>
    public sealed class NumericTextBox : TextBox
    {
        private decimal _minimum;
        private decimal _maximum = 100m;
        private decimal _value;
        private int _decimalPlaces;
        private bool _updatingText;

        public NumericTextBox()
        {
            BorderStyle = BorderStyle.None;
            TextAlign = HorizontalAlignment.Left;
            ShortcutsEnabled = true;
            SetDisplayedValue(_value, raiseEvent: false);
        }

        [DefaultValue(typeof(decimal), "0")]
        public decimal Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (_maximum < _minimum)
                    _maximum = _minimum;
                Value = _value;
            }
        }

        [DefaultValue(typeof(decimal), "100")]
        public decimal Maximum
        {
            get => _maximum;
            set
            {
                _maximum = value;
                if (_minimum > _maximum)
                    _minimum = _maximum;
                Value = _value;
            }
        }

        [DefaultValue(typeof(decimal), "1")]
        public decimal Increment { get; set; } = 1m;

        [DefaultValue(0)]
        public int DecimalPlaces
        {
            get => _decimalPlaces;
            set
            {
                int normalized = Math.Clamp(value, 0, 28);
                if (_decimalPlaces == normalized)
                    return;

                _decimalPlaces = normalized;
                SetDisplayedValue(_value, raiseEvent: false);
            }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public decimal Value
        {
            get => _value;
            set => SetDisplayedValue(value, raiseEvent: true);
        }

        public event EventHandler? ValueChanged;

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (_updatingText)
                return;

            if (TryParse(Text, out decimal parsed) && parsed >= _minimum && parsed <= _maximum && parsed != _value)
            {
                _value = parsed;
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnLeave(EventArgs e)
        {
            CommitText();
            base.OnLeave(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitText();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar))
            {
                base.OnKeyPress(e);
                return;
            }

            string decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            bool isDecimalSeparator = e.KeyChar == '.' || e.KeyChar == ',' || decimalSeparator.Contains(e.KeyChar);
            if (isDecimalSeparator && _decimalPlaces > 0 && !ContainsDecimalSeparator(Text))
            {
                if (decimalSeparator.Length == 1)
                    e.KeyChar = decimalSeparator[0];
                base.OnKeyPress(e);
                return;
            }

            bool isMinus = e.KeyChar == '-' && _minimum < 0 && SelectionStart == 0 && !Text.Contains('-');
            if (isMinus)
            {
                base.OnKeyPress(e);
                return;
            }

            e.Handled = true;
        }

        private void CommitText()
        {
            decimal committed = _value;
            if (TryParse(Text, out decimal parsed))
                committed = Math.Clamp(parsed, _minimum, _maximum);

            SetDisplayedValue(committed, raiseEvent: committed != _value);
        }

        private void SetDisplayedValue(decimal value, bool raiseEvent)
        {
            decimal clamped = Math.Clamp(value, _minimum, _maximum);
            bool changed = clamped != _value;
            _value = clamped;

            string formatted = FormatValue(clamped);
            if (!string.Equals(Text, formatted, StringComparison.Ordinal))
            {
                _updatingText = true;
                try
                {
                    Text = formatted;
                    SelectionStart = TextLength;
                }
                finally
                {
                    _updatingText = false;
                }
            }

            if (raiseEvent && changed)
                ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private string FormatValue(decimal value)
        {
            return value.ToString($"F{_decimalPlaces}", CultureInfo.CurrentCulture);
        }

        private static bool TryParse(string text, out decimal value)
        {
            const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;
            return decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out value)
                || decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out value);
        }

        private static bool ContainsDecimalSeparator(string text)
        {
            string current = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            return text.Contains('.') || text.Contains(',') || (!string.IsNullOrEmpty(current) && text.Contains(current));
        }
    }
}
