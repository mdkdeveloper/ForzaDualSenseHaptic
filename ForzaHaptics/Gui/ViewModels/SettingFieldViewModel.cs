using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ForzaHaptics.Gui.ViewModels;

public abstract class SettingFieldViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private readonly PropertyInfo _property;
    private readonly PropertyInfo[] _path;
    private bool _isEnabled = true;
    private string? _errorMessage;

    protected SettingFieldViewModel(
        SettingsViewModel settings,
        PropertyInfo property,
        UiAttribute ui,
        PropertyInfo[] path)
    {
        _settings = settings;
        _property = property;
        _path = path;
        Label = ui.Label;
        Tip = ui.Tip;
        RequiresRestart = ui.Restart;
        Minimum = ui.Min;
        Maximum = ui.Max;
        Step = ui.Step;
    }

    public string Label { get; }
    public string DisplayLabel => RequiresRestart ? Label + " ⟳" : Label;
    public string? Tip { get; }
    public string? DisplayTip => RequiresRestart
        ? string.Join(Environment.NewLine, new[] { Tip, "Takes effect after restarting the output." }.Where(value => !string.IsNullOrEmpty(value)))
        : Tip;
    public bool RequiresRestart { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    public string PropertyName => _property.Name;
    public Type ValueType => _property.PropertyType;

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            bool hadError = HasError;
            if (!SetProperty(ref _errorMessage, value))
                return;
            OnPropertyChanged(nameof(HasError));
            if (hadError != HasError)
                _settings.NotifyValidationChanged();
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    internal object? ModelValue => GetModelValue();

    internal abstract void RefreshFromModel();

    protected object? GetModelValue() => _property.GetValue(GetOwner());

    protected bool TrySetModelValue(object? value)
    {
        object owner = GetOwner();
        object? previous = _property.GetValue(owner);
        if (ValuesEqual(previous, value))
        {
            ClearError();
            return true;
        }

        try
        {
            _property.SetValue(owner, value);
            AppConfig validationCopy = ConfigManager.Clone(_settings.Working);
            validationCopy.Validate();
        }
        catch (Exception exception)
        {
            _property.SetValue(owner, previous);
            ErrorMessage = Unwrap(exception).Message;
            return false;
        }

        ClearError();
        _settings.NotifyFieldChanged(this);
        return true;
    }

    protected void ClearError() => ErrorMessage = null;

    protected void SetError(string message) => ErrorMessage = message;

    protected static bool TryParseNumber(string? text, out double value) =>
        double.TryParse(
            (text ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    protected object ConvertNumber(double value) =>
        ValueType == typeof(int) ? checked((object)(int)Math.Round(value)) :
        ValueType == typeof(float) ? checked((object)(float)value) :
        (object)value;

    internal static bool IsNumericType(Type type) =>
        type == typeof(int) || type == typeof(float) || type == typeof(double);

    private object GetOwner()
    {
        object owner = _settings.Working;
        for (int index = 0; index < _path.Length - 1; index++)
        {
            PropertyInfo segment = _path[index];
            object? next = segment.GetValue(owner);
            if (next is null)
            {
                next = Activator.CreateInstance(segment.PropertyType)
                    ?? throw new InvalidOperationException($"Cannot create settings section {segment.PropertyType.Name}.");
                segment.SetValue(owner, next);
            }
            owner = next;
        }
        return owner;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is int[] leftIntegers && right is int[] rightIntegers)
            return leftIntegers.SequenceEqual(rightIntegers);
        if (left is IEnumerable<string> leftStrings && right is IEnumerable<string> rightStrings)
            return leftStrings.SequenceEqual(rightStrings);
        return Equals(left, right);
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException!
            : exception;
}
