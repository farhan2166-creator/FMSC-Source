using System.Globalization;
namespace FMSC_SMS.Core
{
    public sealed class ModernNumericTextBox : ModernTextBox
    {
        private decimal _value;
        private decimal _minimum;
        private decimal _maximum = 100000000m;
        private int _decimalPlaces;
        private bool _thousandsSeparator;
        private bool _trimTrailingZeros;
        private bool _refreshingDisplay;
        /* Pending text is committed by the Value getter before callers read it. */
        private bool _textEdited;
        private readonly ToolTip _invalidValueTip = new() { AutoPopDelay = 3000, InitialDelay = 0, ReshowDelay = 0 };
        public event EventHandler? ValueChanged;
        public ModernNumericTextBox()
        {
            RefreshDisplay();
            GotFocus += (_, _) => PlaceholderText = "";
            LostFocus += (_, _) => UpdatePlaceholder();
            WholeNumberInput.Attach(this,()=>Minimum<0);
            Leave += (_, _) => CommitFromText();
            TextChanged += (_, _) => { if (!_refreshingDisplay) _textEdited = true; };
            Disposed += (_, _) => _invalidValueTip.Dispose();
        }
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
        public int DecimalPlaces
        {
            get => _decimalPlaces;
            set
            {
                _decimalPlaces = 0;
                RefreshDisplay();
            }
        }
        public bool ThousandsSeparator
        {
            get => _thousandsSeparator;
            set
            {
                _thousandsSeparator = value;
                RefreshDisplay();
            }
        }
        // Preserve a fractional draft so write validation can reject it, never round it.
        public bool WholeRupeesOnly { get; set; } = true;
        public decimal Increment { get; set; } = 1m;
        public bool TrimTrailingZeros
        {
            get => _trimTrailingZeros;
            set { _trimTrailingZeros = value; RefreshDisplay(); }
        }
        public decimal Value
        {
            get
            {
                if (_textEdited)
                    CommitFromText();
                return _value;
            }
            set
            {
                decimal clamped = Clamp(value);
                bool changed = clamped != _value;
                _value = clamped;
                RefreshDisplay();
                if (changed)
                    ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        private decimal Clamp(decimal v)
        {
            if (v < _minimum)
                v = _minimum;
            if (v > _maximum)
                v = _maximum;
            return v;
        }
        private void RefreshDisplay()
        {
            _refreshingDisplay = true;
            Text = _value == 0 ? "" : FormatValue(_value);
            UpdatePlaceholder();
            _refreshingDisplay = false;
            _textEdited = false;
        }
        private void UpdatePlaceholder() => PlaceholderText = ContainsFocus ? "" : FormatValue(0);
        private string FormatValue(decimal v)
        {
            string format = _thousandsSeparator ? "#,0" : "0";
            if (_decimalPlaces > 0)
                format += "." + new string(_trimTrailingZeros ? '#' : '0', _decimalPlaces);
            if (!WholeRupees.IsWhole(v)) format = _thousandsSeparator ? "#,0.############################" : "0.############################";
            return v.ToString(format, CultureInfo.InvariantCulture);
        }
        private void CommitFromText()
        {
            if (_refreshingDisplay)
                return;
            _textEdited = false;
            string raw = Text.Trim();
            if (raw.Length == 0) { Value = 0; return; }
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
            {
                parsed = _value;
                // Programmatic text assignments also need a safe parsing fallback.
                if (raw.Length > 0)
                {
                    try
                    {
                        _invalidValueTip.Show("That value wasn't understood - reverted to the previous amount.", this, 0, Height, 3000);
                    }
                    catch (Exception nonFatalException16)
                    {
                        FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException16, "ModernNumericTextBox: CommitFromText fallback");
                        // Tooltip display is best-effort only.
                    }
                }
            }
            /* A number outside the allowed range used to snap to the limit with no explanation - the person saw 100 appear where they typed 500. Say so. */
            if (raw.Length > 0 && (parsed < _minimum || parsed > _maximum))
            {
                try
                {
                    _invalidValueTip.Show(
                        parsed > _maximum
                            ? $"The largest value allowed here is {_maximum.ToString(_thousandsSeparator ? "#,0.##" : "0.##", CultureInfo.InvariantCulture)}."
                            : $"The smallest value allowed here is {_minimum.ToString(_thousandsSeparator ? "#,0.##" : "0.##", CultureInfo.InvariantCulture)}.",
                        this, 0, Height, 3500);
                }
                catch (Exception nonFatalException17)
                {
                    FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException17, "ModernNumericTextBox: CommitFromText fallback");
                    // Tooltip display is best-effort only.
                }
            }
            Value = parsed;
        }
    }
}
