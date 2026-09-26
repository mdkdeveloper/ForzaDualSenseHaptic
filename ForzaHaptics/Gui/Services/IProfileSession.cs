namespace ForzaHaptics.Gui.Services;

/// <summary>Coordinates profile files and the live configuration independently from the desktop UI.</summary>
public interface IProfileSession : IDisposable
{
    IReadOnlyList<string> Profiles { get; }
    string ActiveProfile { get; }
    string ProfileDirectory { get; }
    AppConfig Current { get; }

    event EventHandler? ReloadedFromDisk;

    void Refresh();
    void Apply(AppConfig config);
    void Save();
    void Revert();
    void SwitchTo(string profileName);
    string? ValidateNewName(string? profileName);
    void Create(string profileName, AppConfig config);
    void Duplicate(string sourceProfileName, string profileName);
    void Rename(string profileName);
    void Delete(string profileName);
}

public sealed class ProfileSession : IProfileSession
{
    private readonly ProfileStore _store;
    private readonly ConfigManager _config;
    private IReadOnlyList<string> _profiles;
    private bool _disposed;

    public ProfileSession(ProfileStore store, ConfigManager config, string activeProfile)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ActiveProfile = activeProfile ?? throw new ArgumentNullException(nameof(activeProfile));
        _profiles = store.List();
        _config.ReloadedFromDisk += OnReloadedFromDisk;
    }

    public IReadOnlyList<string> Profiles => _profiles;
    public string ActiveProfile { get; private set; }
    public string ProfileDirectory => _store.Directory;
    public AppConfig Current => _config.Current;

    public event EventHandler? ReloadedFromDisk;

    public void Refresh() => _profiles = _store.List();

    public void Apply(AppConfig config) => _config.Apply(config);

    public void Save() => _config.Save();

    public void Revert() => _config.Revert();

    public void SwitchTo(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        _config.SwitchTo(_store.PathOf(profileName));
        ActiveProfile = profileName;
        _store.SaveActive(profileName);
        Refresh();
    }

    public string? ValidateNewName(string? profileName) => _store.CheckNewName(profileName);

    public void Create(string profileName, AppConfig config)
    {
        _store.Create(profileName, config);
        Refresh();
    }

    public void Duplicate(string sourceProfileName, string profileName)
    {
        _store.Duplicate(sourceProfileName, profileName);
        Refresh();
    }

    public void Rename(string profileName)
    {
        string previousName = ActiveProfile;
        _store.Rename(previousName, profileName);
        SwitchTo(profileName);
    }

    public void Delete(string profileName)
    {
        _store.Delete(profileName);
        Refresh();
    }

    private void OnReloadedFromDisk() => ReloadedFromDisk?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _config.ReloadedFromDisk -= OnReloadedFromDisk;
        _config.Dispose();
    }
}
