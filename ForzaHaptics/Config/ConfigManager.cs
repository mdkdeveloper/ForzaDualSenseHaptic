using System.Text.Json;
using System.Text.Json.Serialization;
using ForzaHaptics.Util;

namespace ForzaHaptics;

/// <summary>
/// Current configuration: reads from a profile file, reloads automatically after external changes,
/// applies live changes from the window, and saves them.
/// </summary>
public sealed class ConfigManager : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _sync = new();
    private AppConfig _current = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private double _ignoreWatcherUntil;

    public string? Path { get; private set; }
    public bool IsReadOnly => Path == null;
    // Never expose the factory configuration as a mutable shared object.
    public AppConfig Current => IsReadOnly ? new AppConfig() : Volatile.Read(ref _current);

    /// <summary>The file was changed externally and reloaded (raised from a background thread).</summary>
    public event Action? ReloadedFromDisk;

    private ConfigManager(string? path)
    {
        Path = path;
    }

    public static ConfigManager OpenDefault() => new(null);

    public void SwitchToDefault()
    {
        lock (_sync)
        {
            StopWatching();
            Path = null;
            Volatile.Write(ref _current, new AppConfig());
        }
    }

    /// <summary>Opens a configuration file; creates one with default values if it does not exist.</summary>
    public static ConfigManager Open(string path, bool createIfMissing = true)
    {
        var manager = new ConfigManager(System.IO.Path.GetFullPath(path));
        if (!File.Exists(manager.Path))
        {
            if (!createIfMissing) throw new FileNotFoundException("Configuration file not found", manager.Path);
            File.WriteAllText(manager.Path!, Serialize(new AppConfig()));
            Log.Warn($"Configuration not found; created a default one: {manager.Path}");
        }

        manager._current = Parse(ReadWithRetry(manager.Path!));
        Log.Info($"Configuration: {manager.Path}");
        manager.StartWatching();
        return manager;
    }

    /// <summary>Switches to another file (profile).</summary>
    public void SwitchTo(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var cfg = Parse(ReadWithRetry(path));
        lock (_sync)
        {
            StopWatching();
            Path = path;
            _ignoreWatcherUntil = 0;
            Volatile.Write(ref _current, cfg);
            StartWatching();
        }
        Log.Ok($"Profile: {System.IO.Path.GetFileNameWithoutExtension(path)}");
    }

    /// <summary>Applies settings in memory without writing the file. Throws if any value is invalid.</summary>
    public void Apply(AppConfig cfg)
    {
        lock (_sync)
        {
            EnsureEditable();
            var copy = Clone(cfg);
            copy.Validate();
            Volatile.Write(ref _current, copy);
        }
    }

    /// <summary>Writes the current settings to the profile file.</summary>
    public void Save()
    {
        lock (_sync)
        {
            EnsureEditable();
            _ignoreWatcherUntil = Clock.Now + 1.0;
            File.WriteAllText(Path!, Serialize(Current));
        }
    }

    /// <summary>Discards unsaved changes by reloading the file.</summary>
    public void Revert()
    {
        lock (_sync)
        {
            if (IsReadOnly) return;
            Volatile.Write(ref _current, Parse(ReadWithRetry(Path!)));
        }
    }

    private void EnsureEditable()
    {
        if (IsReadOnly) throw new InvalidOperationException("Default is read-only. Duplicate it to create an editable profile.");
    }

    public static AppConfig Clone(AppConfig cfg) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg, JsonOptions), JsonOptions) ?? new AppConfig();

    public static string Serialize(AppConfig cfg) => JsonSerializer.Serialize(cfg, JsonOptions);

    public static AppConfig Parse(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        cfg.Validate();
        return cfg;
    }

    public static string ReadWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50); // the editor is still holding the file
            }
        }
    }

    private void StartWatching()
    {
        if (IsReadOnly) return;
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (dir == null) return;
        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        var debounce = _debounce;
        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path!))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        void ScheduleReload()
        {
            try { debounce.Change(250, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* A profile switch disposed this watcher. */ }
        }
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
        _watcher.EnableRaisingEvents = true;
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    private void Reload()
    {
        string path;
        lock (_sync)
        {
            if (IsReadOnly || Clock.Now < _ignoreWatcherUntil) return;
            path = Path!;
        }
        if (!File.Exists(path)) return;

        try
        {
            var cfg = Parse(ReadWithRetry(path));
            lock (_sync)
            {
                if (path != Path) return;
                Volatile.Write(ref _current, cfg);
            }
            Log.Ok($"{System.IO.Path.GetFileName(path)} reloaded ✓");
            ReloadedFromDisk?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"Error in {System.IO.Path.GetFileName(path)}; keeping the previous settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_sync) StopWatching();
    }
}
