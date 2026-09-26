using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;

namespace ForzaHaptics.Gui.ViewModels;

public sealed class BoolSettingFieldViewModel : SettingFieldViewModel
{
    private bool _value;

    internal BoolSettingFieldViewModel(SettingsViewModel settings, PropertyInfo property, UiAttribute ui, PropertyInfo[] path)
        : base(settings, property, ui, path)
    {
    }

    public bool Value
    {
        get => _value;
        set
        {
            if (_value == value || !TrySetModelValue(value))
                return;
            SetProperty(ref _value, value);
        }
    }

    internal override void RefreshFromModel()
    {
        SetProperty(ref _value, (bool)(GetModelValue() ?? false), nameof(Value));
        ClearError();
    }
}

public sealed class ChoiceSettingFieldViewModel : SettingFieldViewModel
{
    private readonly ReadOnlyCollection<string> _choices;
    private string? _selectedValue;

    internal ChoiceSettingFieldViewModel(
        SettingsViewModel settings,
        PropertyInfo property,
        UiAttribute ui,
        PropertyInfo[] path,
        IEnumerable<string> choices)
        : base(settings, property, ui, path)
    {
        _choices = Array.AsReadOnly(choices.ToArray());
    }

    public ReadOnlyCollection<string> Choices => _choices;

    public string? SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (string.Equals(_selectedValue, value, StringComparison.Ordinal) || value is null)
                return;

            object converted;
            try
            {
                converted = ValueType.IsEnum
                    ? Enum.Parse(ValueType, value, ignoreCase: true)
                    : value;
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
                return;
            }

            if (!TrySetModelValue(converted))
                return;
            SetProperty(ref _selectedValue, value);
        }
    }

    internal override void RefreshFromModel()
    {
        string? current = GetModelValue()?.ToString();
        string? selected = _choices.FirstOrDefault(choice =>
            string.Equals(choice, current, StringComparison.OrdinalIgnoreCase));
        SetProperty(ref _selectedValue, selected, nameof(SelectedValue));
        ClearError();
    }
}

public sealed class SliderSettingFieldViewModel : SettingFieldViewModel
{
    private readonly string _format;
    private double _value;
    private string _text = string.Empty;

    internal SliderSettingFieldViewModel(SettingsViewModel settings, PropertyInfo property, UiAttribute ui, PropertyInfo[] path)
        : base(settings, property, ui, path)
    {
        _format = GetFormat(property.PropertyType, ui.Step);
        CommitCommand = new RelayCommand(CommitText);
    }

    public double Value
    {
        get => _value;
        set
        {
            double normalized = Normalize(value);
            if (Math.Abs(_value - normalized) < 1e-9)
                return;
            if (!TrySetModelValue(ConvertNumber(normalized)))
                return;
            SetProperty(ref _value, normalized);
            SetProperty(ref _text, Format(normalized), nameof(Text));
        }
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public IRelayCommand CommitCommand { get; }

    public bool TryCommitText()
    {
        if (!TryParseNumber(Text, out double parsed))
        {
            SetError("Enter a valid number.");
            return false;
        }

        double normalized = Normalize(parsed);
        if (!TrySetModelValue(ConvertNumber(normalized)))
            return false;
        SetProperty(ref _value, normalized, nameof(Value));
        SetProperty(ref _text, Format(normalized), nameof(Text));
        return true;
    }

    internal override void RefreshFromModel()
    {
        double value = Convert.ToDouble(GetModelValue(), CultureInfo.InvariantCulture);
        SetProperty(ref _value, value, nameof(Value));
        SetProperty(ref _text, Format(value), nameof(Text));
        ClearError();
    }

    private void CommitText() => TryCommitText();

    private double Normalize(double value)
    {
        double clamped = Math.Clamp(value, Minimum, Maximum);
        double snapped = Step > 0 ? Math.Round(clamped / Step) * Step : clamped;
        return Math.Clamp(snapped, Minimum, Maximum);
    }

    private string Format(double value) => value.ToString(_format, CultureInfo.InvariantCulture);

    private static string GetFormat(Type type, double step)
    {
        if (type == typeof(int) || step >= 1)
            return "0";
        int decimals = (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
        return "0." + new string('0', Math.Clamp(decimals, 1, 4));
    }
}

public sealed class TextSettingFieldViewModel : SettingFieldViewModel
{
    private string _text = string.Empty;

    internal TextSettingFieldViewModel(SettingsViewModel settings, PropertyInfo property, UiAttribute ui, PropertyInfo[] path)
        : base(settings, property, ui, path)
    {
        CommitCommand = new RelayCommand(CommitText);
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public IRelayCommand CommitCommand { get; }

    public bool TryCommitText()
    {
        object value;
        if (ValueType == typeof(string))
        {
            value = Text.Trim();
        }
        else
        {
            if (!TryParseNumber(Text, out double number))
            {
                SetError("Enter a valid number.");
                return false;
            }

            try
            {
                value = ConvertNumber(number);
            }
            catch (OverflowException)
            {
                SetError("The number is outside the supported range.");
                return false;
            }
        }

        if (!TrySetModelValue(value))
            return false;
        SetProperty(ref _text, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, nameof(Text));
        return true;
    }

    internal override void RefreshFromModel()
    {
        SetProperty(
            ref _text,
            Convert.ToString(GetModelValue(), CultureInfo.InvariantCulture) ?? string.Empty,
            nameof(Text));
        ClearError();
    }

    private void CommitText() => TryCommitText();
}

public sealed class ListSettingFieldViewModel : SettingFieldViewModel
{
    private string _text = string.Empty;

    internal ListSettingFieldViewModel(SettingsViewModel settings, PropertyInfo property, UiAttribute ui, PropertyInfo[] path)
        : base(settings, property, ui, path)
    {
        CommitCommand = new RelayCommand(CommitText);
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public IRelayCommand CommitCommand { get; }

    public bool TryCommitText()
    {
        string[] parts = Text.Split(
            new[] { ',', ';', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        object value;
        if (ValueType == typeof(int[]))
        {
            var integers = new List<int>();
            foreach (string part in parts)
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                {
                    SetError($"'{part}' is not a valid integer.");
                    return false;
                }
                integers.Add(number);
            }
            value = integers.ToArray();
        }
        else
        {
            value = parts.ToList();
        }

        if (!TrySetModelValue(value))
            return false;
        SetProperty(ref _text, Format(value), nameof(Text));
        return true;
    }

    internal override void RefreshFromModel()
    {
        SetProperty(ref _text, Format(GetModelValue()), nameof(Text));
        ClearError();
    }

    private void CommitText() => TryCommitText();

    private static string Format(object? value) => value switch
    {
        int[] integers => string.Join(", ", integers),
        IEnumerable<string> strings => string.Join(", ", strings),
        _ => string.Empty,
    };
}
