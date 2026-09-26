using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ForzaHaptics.Gui.ViewModels;

/// <summary>
/// Builds a platform-independent settings presentation model from <see cref="UiAttribute"/>
/// and <see cref="UiGroupAttribute"/> metadata.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly string[] TabOrder = { UiTabs.General, UiTabs.Vibration, UiTabs.Triggers };

    private readonly List<SettingFieldViewModel> _fields = new();
    private readonly List<SettingsGroupViewModel> _groups = new();
    private readonly ObservableCollection<SettingsTabViewModel> _tabs = new();
    private bool _isLoading;
    private bool _isDirty;
    private bool _restartRequired;
    private AppConfig _working = new();

    public SettingsViewModel(AppConfig config)
    {
        Build(typeof(AppConfig), Array.Empty<PropertyInfo>());
        Load(config);
    }

    public ReadOnlyObservableCollection<SettingsTabViewModel> Tabs { get; private set; } = null!;

    /// <summary>A private editing copy; callers may safely pass it to live-apply logic.</summary>
    public AppConfig Working
    {
        get => _working;
        private set => SetProperty(ref _working, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool RestartRequired
    {
        get => _restartRequired;
        private set => SetProperty(ref _restartRequired, value);
    }

    public bool HasValidationErrors => _fields.Any(item => item.HasError);

    public event EventHandler<SettingChangedEventArgs>? Changed;

    /// <summary>Loads a deep copy and refreshes every field without emitting change events.</summary>
    public void Load(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Working = ConfigManager.Clone(config);
        _isLoading = true;
        try
        {
            foreach (SettingFieldViewModel field in _fields)
                field.RefreshFromModel();

            UpdateEnabledGroups();
        }
        finally
        {
            _isLoading = false;
        }

        IsDirty = false;
        RestartRequired = false;
        OnPropertyChanged(nameof(HasValidationErrors));
    }

    public void MarkSaved() => IsDirty = false;

    public void MarkOutputRestarted() => RestartRequired = false;

    private void Build(Type type, IReadOnlyList<PropertyInfo> prefix)
    {
        UiGroupAttribute? groupAttribute = type.GetCustomAttribute<UiGroupAttribute>();
        if (groupAttribute is null)
            return;

        var groupsByTitle = new Dictionary<string, SettingsGroupViewModel>();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            UiAttribute? ui = property.GetCustomAttribute<UiAttribute>();
            if (ui is null)
                continue;

            string title = ui.Group ?? groupAttribute.Title;
            if (!groupsByTitle.TryGetValue(title, out SettingsGroupViewModel? group))
            {
                group = new SettingsGroupViewModel(
                    title,
                    ui.Group is null ? groupAttribute.Tip : null);
                groupsByTitle.Add(title, group);
                _groups.Add(group);
            }

            PropertyInfo[] path = prefix.Append(property).ToArray();
            SettingFieldViewModel? field = CreateField(property, ui, path);
            if (field is null)
                continue;

            _fields.Add(field);
            group.Add(field);
        }

        foreach (SettingsGroupViewModel group in groupsByTitle.Values
                     .OrderBy(group => group.Title == groupAttribute.Title ? 1 : 0))
        {
            GetOrCreateTab(groupAttribute.Tab).Add(group);
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType.GetCustomAttribute<UiGroupAttribute>() is not null)
                Build(property.PropertyType, prefix.Append(property).ToArray());
        }
    }

    private SettingsTabViewModel GetOrCreateTab(string title)
    {
        SettingsTabViewModel? existing = _tabs.FirstOrDefault(tab => tab.Title == title);
        if (existing is not null)
            return existing;

        var tab = new SettingsTabViewModel(title);
        int expectedIndex = Array.IndexOf(TabOrder, title);
        int insertionIndex = _tabs.Count;
        for (int index = 0; index < _tabs.Count; index++)
        {
            int currentIndex = Array.IndexOf(TabOrder, _tabs[index].Title);
            if (currentIndex < 0)
                currentIndex = int.MaxValue;
            if (expectedIndex >= 0 && expectedIndex < currentIndex)
            {
                insertionIndex = index;
                break;
            }
        }

        _tabs.Insert(insertionIndex, tab);
        Tabs ??= new ReadOnlyObservableCollection<SettingsTabViewModel>(_tabs);
        return tab;
    }

    private SettingFieldViewModel? CreateField(PropertyInfo property, UiAttribute ui, PropertyInfo[] path)
    {
        Type type = property.PropertyType;
        if (type == typeof(bool))
            return new BoolSettingFieldViewModel(this, property, ui, path);
        if (type.IsEnum)
            return new ChoiceSettingFieldViewModel(this, property, ui, path, Enum.GetNames(type));
        if (type == typeof(string) && ui.Choices is not null)
            return new ChoiceSettingFieldViewModel(
                this,
                property,
                ui,
                path,
                ui.Choices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (SettingFieldViewModel.IsNumericType(type) && ui.HasSlider)
            return new SliderSettingFieldViewModel(this, property, ui, path);
        if (SettingFieldViewModel.IsNumericType(type) || type == typeof(string))
            return new TextSettingFieldViewModel(this, property, ui, path);
        if (type == typeof(int[]) || type == typeof(List<string>))
            return new ListSettingFieldViewModel(this, property, ui, path);
        return null;
    }

    internal void NotifyFieldChanged(SettingFieldViewModel field)
    {
        if (_isLoading)
            return;

        UpdateEnabledGroups();
        IsDirty = true;
        if (field.RequiresRestart)
            RestartRequired = true;
        Changed?.Invoke(this, new SettingChangedEventArgs(field));
    }

    internal void NotifyValidationChanged() => OnPropertyChanged(nameof(HasValidationErrors));

    private void UpdateEnabledGroups()
    {
        foreach (SettingsGroupViewModel group in _groups)
            group.UpdateEnabledState();
    }
}

public sealed class SettingChangedEventArgs : EventArgs
{
    public SettingChangedEventArgs(SettingFieldViewModel field)
    {
        Field = field;
    }

    public SettingFieldViewModel Field { get; }
    public bool RequiresRestart => Field.RequiresRestart;
}
