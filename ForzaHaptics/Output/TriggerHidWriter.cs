using System.Buffers.Binary;
using System.Diagnostics;
using ForzaHaptics.Haptics;
using ForzaHaptics.Triggers;
using ForzaHaptics.Util;
using HidSharp;

namespace ForzaHaptics.Output;

/// <summary>
/// L2/R2 adaptive triggers: a separate HID output report (USB 0x02, Bluetooth 0x31).
/// Only trigger bits are set in valid_flag0; motors, lightbar, and audio haptics are left untouched,
/// so haptics (USB audio or BT report 0x32) continue to work in parallel.
/// Byte layout follows HorizonHaptics (dualsense/main.py) and pydualsense.
/// </summary>
public sealed class TriggerHidWriter : IDisposable
{
    internal const int UsbReportSize = 48;
    internal const int BtReportSize = 78;
    private const byte FlagTriggers = 0x0C; // right (bit 2) + left (bit 3) trigger

    private readonly HidStream _stream;
    private readonly HapticBus _bus;
    private readonly bool _bluetooth;
    private readonly Thread _thread;
    private volatile bool _running;
    private long _errors;
    private long _sent;
    private TriggerPair? _lastSent;
    private string? _lastError;
    private bool _loggedActiveReport;
    private byte _sequence;

    public string Description { get; }
    public string DeviceId { get; }
    // A successful OS write is not an acknowledgement from the controller.
    public string Health => $"trigger HID: {Interlocked.Read(ref _sent)} writes, {Interlocked.Read(ref _errors)} errors" +
        (Volatile.Read(ref _lastSent) is { } pair ? $" | last written {pair}" : " | no report written") +
        (!_thread.IsAlive && _running ? " | writer stopped" : "") +
        (Volatile.Read(ref _lastError) is { } error ? $" | last error: {error}" : "");

    public TriggerHidWriter(DualSenseHid.Info info, HapticBus bus)
    {
        DeviceId = info.Device.DevicePath;
        if (!info.Device.TryOpen(out HidStream stream))
            throw new IOException("Could not open the DualSense HID device (Steam Input, DS4Windows, or DSX may be holding it)");

        _stream = stream;
        _stream.WriteTimeout = 200;
        _bus = bus;
        _bluetooth = info.IsBluetooth;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "DualSense triggers",
        };
        Description = $"adaptive triggers: {info.Name}, {(_bluetooth ? "Bluetooth report 0x31" : "USB report 0x02")}";
    }

    /// <summary>Report containing trigger effects. Kept separate for testing.</summary>
    internal static byte[] BuildReport(bool bluetooth, TriggerPair pair, byte sequence = 0)
    {
        byte[] buf;
        int common; // offset of the common report section (valid_flag0)
        if (bluetooth)
        {
            buf = new byte[BtReportSize];
            buf[0] = 0x31;
            buf[1] = (byte)((sequence & 15) << 4);
            buf[2] = 0x10;
            common = 3;
        }
        else
        {
            buf = new byte[UsbReportSize];
            buf[0] = 0x02;
            common = 1;
        }

        buf[common] = FlagTriggers;
        pair.R2.WriteTo(buf.AsSpan(common + 10, 11));
        pair.L2.WriteTo(buf.AsSpan(common + 21, 11));

        if (bluetooth)
        {
            uint crc = Crc32.ComputeBluetoothOutput(buf.AsSpan(0, BtReportSize - 4));
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(BtReportSize - 4, 4), crc);
        }
        return buf;
    }

    public void Start()
    {
        _running = true;
        _thread.Start();
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        var scheduler = new TriggerWriteScheduler();
        int consecutiveErrors = 0;

        while (_running)
        {
            var desired = _bus.Triggers;
            double now = clock.Elapsed.TotalSeconds;
            var pair = scheduler.SelectReport(desired, now);
            // Coalesce ordinary changes, but release a trigger immediately. Do not fight other HID writers.
            if (pair != null)
            {
                if (TryWrite(pair, ref consecutiveErrors))
                {
                    scheduler.Written(pair, now);
                }
                else if (consecutiveErrors > 300)
                {
                    Log.Error("Triggers: the controller is not responding; disabling adaptive triggers.");
                    break;
                }
            }
            Thread.Sleep(4);
        }
    }

    private bool TryWrite(TriggerPair pair, ref int consecutiveErrors)
    {
        try
        {
            var report = BuildReport(_bluetooth, pair, _sequence);
            _stream.Write(report);
            Volatile.Write(ref _lastSent, pair);
            Interlocked.Increment(ref _sent);
            if (!_loggedActiveReport && pair != TriggerPair.Off)
            {
                _loggedActiveReport = true;
                Log.Info($"Triggers: first active {(_bluetooth ? "Bluetooth" : "USB")} HID write completed: {pair}. " +
                    "This confirms an OS write, not controller acknowledgement. Report: " + Convert.ToHexString(report));
            }
            _sequence = (byte)((_sequence + 1) & 15);
            consecutiveErrors = 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _errors);
            Volatile.Write(ref _lastError, ex.Message);
            if (++consecutiveErrors == 1) Log.Warn($"Triggers: write error ({ex.Message})");
            return false;
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join(500);
        int ignored = 0;
        TryWrite(TriggerPair.Off, ref ignored); // release the triggers
        _stream.Dispose();
    }
}

/// <summary>Transport scheduling; immutable effects are coalesced independently of telemetry rate.</summary>
internal sealed class TriggerWriteScheduler
{
    private TriggerPair? _last;
    private double _lastWrite = double.NegativeInfinity;
    public bool ShouldWrite(TriggerPair pair, double now) => SelectReport(pair, now) != null;

    public TriggerPair? SelectReport(TriggerPair desired, double now)
    {
        if (desired == _last) return null;
        if (_last == null || now - _lastWrite >= 0.05 - 1e-9) return desired;
        bool leftRelease = desired.L2.Mode == TriggerEffect.ModeOff && _last.L2.Mode != TriggerEffect.ModeOff;
        bool rightRelease = desired.R2.Mode == TriggerEffect.ModeOff && _last.R2.Mode != TriggerEffect.ModeOff;
        if (!leftRelease && !rightRelease) return null;
        // Emergency release must not smuggle an unrelated force increase past the ordinary rate limit.
        return new TriggerPair(leftRelease ? TriggerEffect.Off : _last.L2,
            rightRelease ? TriggerEffect.Off : _last.R2);
    }

    public void Written(TriggerPair pair, double now)
    {
        _last = pair;
        _lastWrite = now;
    }
}
