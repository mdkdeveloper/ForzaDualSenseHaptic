using ForzaHaptics.Util;
using ForzaHaptics.Output;
using HidSharp;

namespace ForzaHaptics.Controllers;

internal sealed record ControllerDevice(string Id, string Name, ControllerTransport Transport, bool CanDisconnect);
internal interface IControllerConnection : IDisposable { byte[]? Read(); }
internal interface IControllerBackend
{
    IReadOnlyList<ControllerDevice> Find();
    IControllerConnection Open(string id);
    void Disconnect(string id);
}

/// <summary>Owns a separate shared HID input stream, independent of the haptic engine.</summary>
public sealed class ControllerService : IControllerService
{
    private readonly IControllerBackend _backend;
    private readonly Func<string> _outputMode;
    private readonly Func<string?> _activeDeviceId;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private readonly Task _monitor;
    private readonly int _pollMs;
    private IControllerConnection? _connection;
    private ControllerSnapshot _snapshot = ControllerSnapshot.Disconnected;
    private int _disconnecting;
    private int _disposed;
    private int _generation;
    public ControllerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public ControllerService(Func<string>? outputMode = null, Func<string?>? activeDeviceId = null)
        : this(new HidControllerBackend(), outputMode, activeDeviceId) { }

    internal ControllerService(IControllerBackend backend, Func<string>? outputMode = null,
        Func<string?>? activeDeviceId = null, int pollMs = 1000)
    {
        _backend = backend;
        _outputMode = outputMode ?? (() => "auto");
        _activeDeviceId = activeDeviceId ?? (() => null);
        _pollMs = pollMs;
        _monitor = Task.Run(MonitorAsync);
    }

    private async Task MonitorAsync()
    {
        var token = _shutdown.Token;
        long lastValidReport = Environment.TickCount64;
        while (!token.IsCancellationRequested)
        {
            int generation;
            lock (_sync) generation = _generation;
            try
            {
                if (Volatile.Read(ref _disconnecting) == 0)
                {
                    var devices = _backend.Find();
                    string? pinned = _activeDeviceId();
                    string mode = _outputMode().Trim().ToLowerInvariant();
                    var candidates = devices.Where(d => mode == "auto" ||
                        (mode == "bt" && d.Transport == ControllerTransport.Bluetooth) ||
                        (mode == "usb" && d.Transport == ControllerTransport.Usb));
                    var selected = pinned != null ? devices.FirstOrDefault(d => d.Id == pinned) :
                        candidates.FirstOrDefault(d => d.Id == Snapshot.DeviceId) ??
                        candidates.OrderBy(d => d.Transport == ControllerTransport.Usb ? 0 : 1).ThenBy(d => d.Id, StringComparer.Ordinal).FirstOrDefault();
                    bool needsOpen;
                    lock (_sync)
                    {
                        if (_generation != generation || _disconnecting != 0 || _disposed != 0) continue;
                        if (selected == null)
                        {
                            CloseConnection();
                            Volatile.Write(ref _snapshot, ControllerSnapshot.Disconnected);
                        }
                        else
                        {
                            if (Snapshot.DeviceId != selected.Id)
                            {
                                CloseConnection();
                                lastValidReport = Environment.TickCount64;
                                Volatile.Write(ref _snapshot, new ControllerSnapshot
                                {
                                    DeviceId = selected.Id, Name = selected.Name, IsConnected = true,
                                    Transport = selected.Transport, CanDisconnect = selected.CanDisconnect,
                                });
                            }
                            else Volatile.Write(ref _snapshot, Snapshot with { CanDisconnect = selected.CanDisconnect });
                        }
                        needsOpen = selected != null && _connection == null;
                    }
                    if (selected != null)
                    {
                        try
                        {
                            if (needsOpen)
                            {
                                // HID open can block. Never hold the state lock across a driver call.
                                var opened = _backend.Open(selected.Id);
                                lock (_sync)
                                {
                                    if (_generation != generation || _disconnecting != 0 || _disposed != 0)
                                    {
                                        opened.Dispose();
                                        continue;
                                    }
                                    _connection = opened;
                                }
                            }
                            var deadline = Environment.TickCount64 + _pollMs;
                            do
                            {
                                IControllerConnection? connection;
                                lock (_sync) connection = _connection;
                                if (connection == null) break;
                                var report = connection.Read();
                                bool valid = report != null && DualSenseBatteryParser.TryParse(report, selected.Transport, out _, out _);
                                lock (_sync)
                                {
                                    if (_generation != generation || _disconnecting != 0 || _disposed != 0) break;
                                    if (valid)
                                    {
                                        DualSenseBatteryParser.TryParse(report, selected.Transport, out int? percent, out bool charging);
                                        DualSenseTriggerFeedbackParser.TryParse(report, selected.Transport, out var feedback);
                                        DualSensePhysicalInputParser.TryParse(report, selected.Transport, out byte left, out byte right);
                                        lastValidReport = Environment.TickCount64;
                                        Volatile.Write(ref _snapshot, Snapshot with { BatteryPercent = percent, IsCharging = charging,
                                            TriggerFeedback = feedback, LeftTrigger = left, RightTrigger = right, LastInputTick = lastValidReport });
                                    }
                                    else if (Environment.TickCount64 - lastValidReport > 5000)
                                        Volatile.Write(ref _snapshot, Snapshot with { BatteryPercent = null, IsCharging = false, TriggerFeedback = null });
                                    else if (Environment.TickCount64 - lastValidReport > 300)
                                        Volatile.Write(ref _snapshot, Snapshot with { TriggerFeedback = null });
                                }
                            } while (!token.IsCancellationRequested && Volatile.Read(ref _disconnecting) == 0 && Environment.TickCount64 < deadline);
                            continue;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or TimeoutException)
                        {
                            lock (_sync)
                            {
                                if (_generation == generation && _disconnecting == 0)
                                {
                                    CloseConnection();
                                    // Discovery owns connection status. Read failures invalidate reported input data.
                                    Volatile.Write(ref _snapshot, Snapshot with { BatteryPercent = null, IsCharging = false, TriggerFeedback = null, LastInputTick = 0 });
                                }
                            }
                        }
                    }
                }
                await Task.Delay(_pollMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn($"Controller monitoring: {ex.Message}");
                lock (_sync)
                {
                    if (_generation == generation && _disconnecting == 0)
                    {
                        CloseConnection();
                        Volatile.Write(ref _snapshot, ControllerSnapshot.Disconnected);
                    }
                }
                try { await Task.Delay(_pollMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        CloseConnection();
    }

    public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_sync)
        {
            if (_disconnecting != 0) throw new InvalidOperationException("A controller disconnect is already in progress.");
            _disconnecting = 1;
            _generation++;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Snapshot;
            if (!current.IsConnected || current.DeviceId != deviceId)
                throw new InvalidOperationException("The selected controller is no longer connected.");
            if (current.Transport != ControllerTransport.Bluetooth || !current.CanDisconnect)
                throw new InvalidOperationException("Only a Bluetooth controller with a known device address can be disconnected.");
            CloseConnection();
            await Task.Run(() => _backend.Disconnect(deviceId), cancellationToken).ConfigureAwait(false);
            // Do not pretend a successful IOCTL guarantees that Windows removed the HID device.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (!_backend.Find().Any(d => d.Id == deviceId))
                {
                    Volatile.Write(ref _snapshot, ControllerSnapshot.Disconnected);
                    return;
                }
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            throw new IOException("Windows accepted the disconnect request, but the controller is still connected.");
        }
        catch (Exception ex)
        {
            Log.Error($"Controller disconnect: {ex.Message}");
            throw;
        }
        finally { lock (_sync) { _generation++; Volatile.Write(ref _disconnecting, 0); } }
    }

    private void CloseConnection()
    {
        lock (_sync)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        CloseConnection();
        // A blocking device enumeration/open is owned by the worker; keep shutdown bounded.
        if (_monitor.Wait(1500)) _shutdown.Dispose();
        else _ = _monitor.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
    }
}

internal sealed class HidControllerBackend : IControllerBackend
{
    public IReadOnlyList<ControllerDevice> Find() => DualSenseHid.Find().Select(info => new ControllerDevice(
        info.Device.DevicePath, info.Device.ProductID == 0x0DF2 ? "DualSense Edge" : "DualSense",
        info.IsBluetooth ? ControllerTransport.Bluetooth : ControllerTransport.Usb,
        info.IsBluetooth && BluetoothControllerDisconnect.TryParseAddress(SafeSerial(info.Device), out _))).ToList();

    internal static string SafeSerial(HidDevice device)
    {
        try { return device.GetSerialNumber(); } catch { return ""; }
    }

    public IControllerConnection Open(string id)
    {
        var info = DualSenseHid.Find().FirstOrDefault(i => i.Device.DevicePath == id)
            ?? throw new IOException("Controller disconnected.");
        if (!info.Device.TryOpen(out HidStream stream)) throw new IOException("Could not open controller input.");
        stream.ReadTimeout = 250;
        return new HidConnection(stream, info.MaxInput);
    }

    public void Disconnect(string id)
    {
        var info = DualSenseHid.Find().FirstOrDefault(i => i.Device.DevicePath == id && i.IsBluetooth)
            ?? throw new IOException("Bluetooth controller disconnected or changed.");
        if (!BluetoothControllerDisconnect.TryParseAddress(SafeSerial(info.Device), out ulong address))
            throw new IOException("The controller's Bluetooth address is unavailable.");
        BluetoothControllerDisconnect.Disconnect(address);
    }

    private sealed class HidConnection(HidStream stream, int reportLength) : IControllerConnection
    {
        private readonly byte[] _buffer = new byte[reportLength];
        public byte[]? Read()
        {
            try
            {
                int count = stream.Read(_buffer, 0, _buffer.Length);
                return _buffer.AsSpan(0, count).ToArray();
            }
            catch (TimeoutException) { return null; }
        }
        public void Dispose() => stream.Dispose();
    }
}
