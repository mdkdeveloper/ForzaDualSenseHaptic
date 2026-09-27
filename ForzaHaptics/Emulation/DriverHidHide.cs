using System.Text.Json;

namespace ForzaHaptics.Emulation;

public interface IHidHideService : IDisposable
{
    void CheckAvailable();
    void Recover();
    void AllowApplication();
    void Hide(string devicePath);
    void Restore();
}

// Each session holds HidHide's exclusive control handle for a complete read/modify/write transaction.
public interface IHidHideControl : IDisposable
{
    List<string> Applications { get; set; }
    List<string> Devices { get; set; }
    bool Active { get; set; }
    bool Inverse { get; }
}

public sealed class HidHideService : IHidHideService
{
    private readonly Func<IHidHideControl> _open;
    private readonly Func<string, string> _instanceId;
    private readonly Func<string> _application;
    private readonly string _journalPath;
    private readonly object _gate = new();
    private Journal? _journal;

    public HidHideService() : this(() => new HidHideControl(), HidHideNative.ResolveInstanceId,
        () => HidHideNative.ToDevicePath(Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot identify the current executable.")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForzaHaptics", "hidhide-recovery.json")) { }

    public HidHideService(Func<IHidHideControl> open, Func<string, string> instanceId,
        Func<string> application, string journalPath)
    {
        _open = open;
        _instanceId = instanceId;
        _application = application;
        _journalPath = journalPath;
    }

    public void CheckAvailable()
    {
        using var control = _open();
        RequireNormalWhitelist(control);
    }

    private static void RequireNormalWhitelist(IHidHideControl control)
    {
        if (control.Inverse)
            throw new InvalidOperationException("HidHide uses an inverted application list. Disable inverse application cloak in HidHide before enabling Xbox emulation.");
    }

    public void Recover() => Restore();

    public void AllowApplication()
    {
        lock (_gate)
        {
            using var control = _open();
            RequireNormalWhitelist(control);
            if (_journal == null && File.Exists(_journalPath))
                throw new InvalidOperationException("HidHide recovery is pending. Recover the previous session before enabling emulation.");
            _journal ??= new Journal { OriginalActive = control.Active, OriginalDevices = control.Devices };
            var app = _application();
            var apps = control.Applications;
            if (apps.Contains(app, StringComparer.OrdinalIgnoreCase)) return;
            _journal.AddedApplications.Add(app);
            Save(); // Write-ahead: recovery is safe even if the following IOCTL fails.
            apps.Add(app);
            control.Applications = apps;
        }
    }

    public void Hide(string devicePath)
    {
        lock (_gate)
        {
            if (_journal == null) throw new InvalidOperationException("Allow the application before hiding its controller.");
            var instance = _instanceId(devicePath);
            if (!instance.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the selected HID controller interface can be hidden.");
            using var control = _open();
            RequireNormalWhitelist(control);
            var devices = control.Devices;
            if (!control.Active && devices.Any(d => !string.Equals(d, instance, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("HidHide is inactive and has other configured devices. Review and enable the existing hiding configuration in HidHide before enabling Xbox emulation; this application will not hide unrelated devices.");
            if (!devices.Contains(instance, StringComparer.OrdinalIgnoreCase))
            {
                _journal.AddedDevices.Add(instance);
                Save();
                devices.Add(instance);
                control.Devices = devices;
            }
            if (!control.Active)
            {
                _journal.Activated = true;
                Save();
                control.Active = true;
            }
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (_journal == null && File.Exists(_journalPath))
            {
                try
                {
                    var recovered = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath));
                    if (recovered == null || recovered.OriginalDevices == null || recovered.AddedDevices == null
                        || recovered.AddedApplications == null
                        || recovered.OriginalDevices.Concat(recovered.AddedDevices).Concat(recovered.AddedApplications)
                            .Any(string.IsNullOrWhiteSpace))
                        throw new JsonException("Invalid recovery entries.");
                    _journal = recovered;
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("The HidHide recovery journal is invalid. Restore the controller using HidHide Configuration Client, then remove the recovery journal before retrying.", ex);
                }
            }
            if (_journal == null) return;
            using var control = _open();
            var devices = control.Devices;
            if (devices.RemoveAll(d => _journal.AddedDevices.Contains(d, StringComparer.OrdinalIgnoreCase)) > 0)
                control.Devices = devices;
            // Do not deactivate hiding if another application has added a device since our session began.
            if (_journal.Activated && !_journal.OriginalActive && control.Active
                && new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(_journal.OriginalDevices))
                control.Active = false;
            var apps = control.Applications;
            if (apps.RemoveAll(a => _journal.AddedApplications.Contains(a, StringComparer.OrdinalIgnoreCase)) > 0)
                control.Applications = apps;
            if (File.Exists(_journalPath)) File.Delete(_journalPath);
            _journal = null;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temp = _journalPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, _journal);
            stream.Flush(true);
        }
        File.Move(temp, _journalPath, true);
    }

    public void Dispose() => Restore();

    public sealed class Journal
    {
        public bool OriginalActive { get; set; }
        public bool Activated { get; set; }
        public List<string> OriginalDevices { get; set; } = [];
        public List<string> AddedDevices { get; set; } = [];
        public List<string> AddedApplications { get; set; } = [];
    }
}