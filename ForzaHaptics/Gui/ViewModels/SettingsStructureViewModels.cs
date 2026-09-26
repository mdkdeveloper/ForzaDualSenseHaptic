using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ForzaHaptics.Gui.ViewModels;

public sealed class SettingsTabViewModel : ObservableObject
{
    private readonly ObservableCollection<SettingsGroupViewModel> _groups = new();

    internal SettingsTabViewModel(string title)
    {
        Title = title;
        Groups = new ReadOnlyObservableCollection<SettingsGroupViewModel>(_groups);
    }

    public string Title { get; }
    public ReadOnlyObservableCollection<SettingsGroupViewModel> Groups { get; }

    internal void Add(SettingsGroupViewModel group) => _groups.Add(group);
}

public sealed class SettingsGroupViewModel : ObservableObject
{
    private readonly ObservableCollection<SettingFieldViewModel> _fields = new();
    private bool _isContentEnabled = true;

    internal SettingsGroupViewModel(string title, string? tip)
    {
        Title = title;
        Tip = tip;
        Fields = new ReadOnlyObservableCollection<SettingFieldViewModel>(_fields);
    }

    public string Title { get; }
    public string? Tip { get; }
    public ReadOnlyObservableCollection<SettingFieldViewModel> Fields { get; }

    /// <summary>Whether fields other than the group's Enabled toggle can be edited.</summary>
    public bool IsContentEnabled
    {
        get => _isContentEnabled;
        private set => SetProperty(ref _isContentEnabled, value);
    }

    internal void Add(SettingFieldViewModel field) => _fields.Add(field);

    internal void UpdateEnabledState(bool isReadOnly = false)
    {
        BoolSettingFieldViewModel? toggle = _fields
            .OfType<BoolSettingFieldViewModel>()
            .FirstOrDefault(field => field.PropertyName == "Enabled");
        bool enabled = toggle?.ModelValue is bool value ? value : true;
        IsContentEnabled = enabled && !isReadOnly;
        foreach (SettingFieldViewModel field in _fields)
            field.IsEnabled = !isReadOnly && (field == toggle || enabled);
    }
}
