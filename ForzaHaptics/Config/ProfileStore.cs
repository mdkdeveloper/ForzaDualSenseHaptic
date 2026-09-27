using System.Text.Json;
using ForzaHaptics.Util;

namespace ForzaHaptics;

/// <summary>Editable JSON profiles beside the executable and a virtual, immutable factory Default.</summary>
public sealed class ProfileStore
{
    public const string DefaultName = "Default";
    public const string InitialProfileName = "profile_1";
    private readonly string _settingsPath;
    private readonly object _settingsGate = new();

    public string Directory { get; }

    private ProfileStore(string directory, string settingsPath)
    {
        Directory = directory;
        _settingsPath = settingsPath;
    }

    public static bool IsDefault(string name) => string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase);

    public static ProfileStore Open(string? baseDirectory = null)
    {
        string baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var store = new ProfileStore(Path.Combine(baseDir, "Configs"), Path.Combine(baseDir, "ForzaHaptics.settings.json"));
        System.IO.Directory.CreateDirectory(store.Directory);
        store.MigrateDefaultFiles();
        if (!store.Exists(InitialProfileName))
        {
            try
            {
                using var seed = typeof(ProfileStore).Assembly.GetManifestResourceStream("ForzaHaptics.Configs.profile_1.json")
                    ?? throw new InvalidOperationException("The bundled profile_1 template is missing.");
                using var target = new FileStream(store.PathOf(InitialProfileName), FileMode.CreateNew, FileAccess.Write);
                seed.CopyTo(target);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not create profile_1; factory Default remains available: {ex.Message}");
            }
        }
        return store;
    }

    private IEnumerable<string> ProfileFiles() => System.IO.Directory.EnumerateFiles(Directory)
        .Where(p => string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> List() => new[] { DefaultName }.Concat(ProfileFiles()
        .Select(Path.GetFileNameWithoutExtension)
        .Where(n => n != null && !IsDefault(n))
        .Select(n => n!)
        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)).ToList();

    public string PathOf(string name)
    {
        ValidateFileName(name);
        if (IsDefault(name)) throw new InvalidOperationException("Default is built in and has no file path.");
        return ProfileFiles().FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(Directory, name + ".json");
    }

    public bool Exists(string name)
    {
        if (IsDefault(name)) return true;
        try { return File.Exists(PathOf(name)); }
        catch (ArgumentException) { return false; }
    }

    public ConfigManager OpenProfile(string name) => IsDefault(name)
        ? ConfigManager.OpenDefault() : ConfigManager.Open(PathOf(name), createIfMissing: false);

    /// <summary>Open the saved selection, then profile_1, then the immutable factory defaults.</summary>
    public ConfigManager OpenActive(out string name)
    {
        foreach (string candidate in new[] { LoadActive(), InitialProfileName, DefaultName }
                     .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manager = OpenProfile(candidate);
                name = IsDefault(candidate) ? DefaultName : Path.GetFileNameWithoutExtension(manager.Path!);
                SaveActive(name);
                return manager;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open profile '{candidate}'; trying the next fallback: {ex.Message}");
            }
        }
        name = DefaultName;
        return ConfigManager.OpenDefault();
    }

    public string ResolveActive()
    {
        using var manager = OpenActive(out string name);
        return name;
    }

    public bool AutoStartListening
    {
        get
        {
            lock (_settingsGate) return LoadSettings().AutoStartListening;
        }
        set => UpdateSettings(settings => settings.AutoStartListening = value, "auto-start listening preference");
    }

    public void SaveActive(string name) => UpdateSettings(settings => settings.ActiveProfile = name, "active profile");

    private void UpdateSettings(Action<Settings> update, string description)
    {
        lock (_settingsGate)
        {
            try
            {
                var settings = LoadSettings();
                update(settings);
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, ConfigManager.JsonOptions));
            }
            catch (Exception ex) { Log.Warn($"Could not remember the {description}: {ex.Message}"); }
        }
    }

    private string? LoadActive()
    {
        lock (_settingsGate) return LoadSettings().ActiveProfile;
    }

    private Settings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new Settings();
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath), ConfigManager.JsonOptions) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the application settings: {ex.Message}");
            return new Settings();
        }
    }

    private void MigrateDefaultFiles()
    {
        string? active = LoadActive();
        foreach (string legacy in ProfileFiles().Where(p => IsDefault(Path.GetFileNameWithoutExtension(p))).ToArray())
        {
            try
            {
                string name = InitialProfileName;
                for (int suffix = 1; Exists(name); suffix++) name = $"Default_imported_{suffix}";
                // Move without parsing or reserializing: even malformed files and comments are preserved.
                File.Move(legacy, PathOf(name));
                if (active != null && IsDefault(active))
                {
                    SaveActive(name);
                    active = name;
                }
                Log.Info($"Preserved legacy Default profile as {name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not migrate legacy Default; the original is preserved at '{legacy}': {ex.Message}");
            }
        }
    }

    private static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || name.EndsWith('.') || name is "." or "..")
            throw new ArgumentException("Name contains invalid characters", nameof(name));
    }

    public string? CheckNewName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name cannot be empty";
        try { ValidateFileName(name); }
        catch (ArgumentException ex) { return ex.Message; }
        if (Exists(name)) return $"Profile \"{name}\" already exists";
        return null;
    }

    private void EnsureNewName(string name)
    {
        string? error = CheckNewName(name);
        if (error != null) throw new InvalidOperationException(error);
    }

    public void Create(string name, AppConfig config)
    {
        EnsureNewName(name);
        var copy = ConfigManager.Clone(config);
        copy.Validate();
        using var writer = new StreamWriter(new FileStream(PathOf(name), FileMode.CreateNew, FileAccess.Write));
        writer.Write(ConfigManager.Serialize(copy));
    }

    public void Duplicate(string source, string name)
    {
        EnsureNewName(name);
        if (IsDefault(source)) Create(name, new AppConfig());
        else File.Copy(PathOf(source), PathOf(name));
    }

    public void Rename(string oldName, string newName)
    {
        if (IsDefault(oldName)) throw new InvalidOperationException("Default cannot be renamed.");
        EnsureNewName(newName);
        File.Move(PathOf(oldName), PathOf(newName));
    }

    public void Delete(string name)
    {
        if (IsDefault(name)) throw new InvalidOperationException("Default cannot be deleted.");
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(PathOf(name),
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private sealed class Settings
    {
        public string? ActiveProfile { get; set; }
        public bool AutoStartListening { get; set; } = true;
    }
}
