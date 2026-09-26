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

    public string Description { get; }
    public string Health => Interlocked.Read(ref _errors) > 0 ? $"trigger errors: {Interlocked.Read(ref _errors)}" : "";

    public TriggerHidWriter(DualSenseHid.Info info, HapticBus bus)
    {
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
    internal static byte[] BuildReport(bool bluetooth, TriggerPair pair)
    {
        byte[] buf;
        int common; // offset of the common report section (valid_flag0)
        if (bluetooth)
        {
            buf = new byte[BtReportSize];
            buf[0] = 0x31;
            buf[1] = 0x02;
            common = 2;
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
        TriggerPair? last = null;
        double lastWrite = double.NegativeInfinity;
        int consecutiveErrors = 0;

        while (_running)
        {
            var pair = _bus.Triggers;
            double now = clock.Elapsed.TotalSeconds;
            // Write on changes and every 0.5 s to restore state after another writer modifies it.
            if (pair != last || now - lastWrite > 0.5)
            {
                if (TryWrite(pair, ref consecutiveErrors))
                {
                    last = pair;
                    lastWrite = now;
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
            _stream.Write(BuildReport(_bluetooth, pair));
            consecutiveErrors = 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _errors);
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
