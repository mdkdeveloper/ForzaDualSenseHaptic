using System.Text.Json;
using ForzaHaptics.Util;

namespace ForzaHaptics;

/// <summary>
/// Settings profiles: *.json files in the Configs folder next to the executable. The list is built by scanning
/// the folder, so a file copied there manually appears after Refresh or a restart.
/// The active profile is stored in ForzaHaptics.settings.json next to the executable.
/// </summary>
public sealed class ProfileStore
{
    private const string DefaultName = "Default";
    private readonly string _settingsPath;

    public string Directory { get; }

    private ProfileStore(string directory, string settingsPath)
    {
        Directory = directory;
        _settingsPath = settingsPath;
    }

    /// <summary>The Configs folder next to the executable; imports a legacy config.json as Default on first launch.</summary>
    public static ProfileStore Open()
    {
        string baseDir = AppContext.BaseDirectory;
        var store = new ProfileStore(Path.Combine(baseDir, "Configs"), Path.Combine(baseDir, "ForzaHaptics.settings.json"));
        System.IO.Directory.CreateDirectory(store.Directory);

        if (store.List().Count == 0)
        {
            string target = store.PathOf(DefaultName);
            string? legacy = ConfigManager.FindLegacyConfig();
            if (legacy != null)
            {
                File.Copy(legacy, target);
                Log.Info($"Created profile {DefaultName} from {legacy}");
            }
            else
            {
                File.WriteAllText(target, ConfigManager.Serialize(new AppConfig()));
                Log.Info($"Created profile {DefaultName} with default settings");
            }
        }
        return store;
    }

    public IReadOnlyList<string> List() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public string PathOf(string name) => Path.Combine(Directory, name + ".json");

    public bool Exists(string name) => File.Exists(PathOf(name));

    /// <summary>The saved active profile if it still exists; otherwise the first profile alphabetically.</summary>
    public string ResolveActive()
    {
        string? saved = LoadActive();
        if (saved != null && Exists(saved)) return saved;
        var all = List();
        return all.Contains(DefaultName) ? DefaultName : all[0];
    }

    public void SaveActive(string name)
    {
        try
        {
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings { ActiveProfile = name }, ConfigManager.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remember the active profile: {ex.Message}");
        }
    }

    private string? LoadActive()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return null;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath), ConfigManager.JsonOptions)?.ActiveProfile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns an error message if the name is unsuitable for a profile file; otherwise null.</summary>
    public string? CheckNewName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return "Name cannot be empty";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            return "Name contains invalid characters";
        if (Exists(name)) return $"Profile \"{name}\" already exists";
        return null;
    }

    public void Create(string name, AppConfig config) => File.WriteAllText(PathOf(name), ConfigManager.Serialize(config));

    /// <summary>Copies the file as-is, including comments.</summary>
    public void Duplicate(string source, string name) => File.Copy(PathOf(source), PathOf(name));

    public void Rename(string oldName, string newName) => File.Move(PathOf(oldName), PathOf(newName));

    public void Delete(string name)
    {
        if (List().Count <= 1) throw new InvalidOperationException("The last profile cannot be deleted");
        // Send it to the Recycle Bin so an accidental deletion can be recovered.
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(PathOf(name),
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private sealed class Settings
    {
        public string? ActiveProfile { get; set; }
    }
}
