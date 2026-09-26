using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ForzaHaptics.Haptics;
using ForzaHaptics.Util;
using HidSharp;

namespace ForzaHaptics.Output;

/// <summary>
/// EXPERIMENTAL. DualSense haptics over Bluetooth: no sound card is exposed, so PCM
/// (8-bit, 3000 Hz, stereo) is packed into HID output report 0x32 in 32-frame chunks (~10.7 ms).
///
/// The packet format was discovered by the SAxense project (Sdore, 2025): https://apps.sdore.me/SAxense
/// https://github.com/egormanga/SAxense — the author requests attribution, so the link is included here.
/// </summary>
public sealed class BluetoothHidOutput : IHapticOutput
{
    public const int SampleRate = 3000;
    // 1-byte report ID + 137 bytes of data + 4-byte CRC-32
    internal const int ReportSize = 142;
    private const int SamplesPerReport = 64;                 // sample bytes (alternating L,R)
    private const int FramesPerReport = SamplesPerReport / 2;
    private const int CounterOffset = 10;
    private const int SamplesOffset = 13;
    private const int CrcOffset = ReportSize - 4;            // CRC is calculated over bytes 0..137

    private readonly HidStream _stream;
    private readonly IHapticSource _source;
    private readonly Thread _thread;
    private readonly byte[] _report = CreateReport();
    private readonly float[] _left = new float[FramesPerReport];
    private readonly float[] _right = new float[FramesPerReport];
    private volatile bool _running;
    private long _sent, _errors;

    public string Description { get; }
    public string Health => Interlocked.Read(ref _errors) > 0 ? $"BT write errors: {Interlocked.Read(ref _errors)}" : "";

    public BluetoothHidOutput(HidDevice device, Func<int, IHapticSource> sourceFactory)
    {
        int maxOut = device.GetMaxOutputReportLength();
        if (maxOut < ReportSize)
            throw new InvalidOperationException($"Device does not accept {ReportSize}-byte reports (maximum {maxOut}); is this definitely a Bluetooth connection?");

        if (!device.TryOpen(out HidStream stream))
            throw new IOException("Could not open the DualSense HID device (HidHide or another application may be hiding it)");

        _stream = stream;
        _stream.WriteTimeout = 200;
        _source = sourceFactory(SampleRate);

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "DualSense BT haptics",
        };
        Description = $"Bluetooth HID (experimental): {DualSenseHid.SafeProductName(device)}, 3000 Hz, report 0x32";
    }

    /// <summary>Empty 0x32 report with populated headers.</summary>
    internal static byte[] CreateReport()
    {
        var report = new byte[ReportSize];
        report[0] = 0x32;          // report id
        report[1] = 0x00;          // tag / seq
        // 0x11 packet (sized): 7 bytes of metadata; the last is a counter
        report[2] = 0x80 | 0x11;
        report[3] = 7;
        report[4] = 0b1111_1110;
        report[9] = 0xFF;
        // 0x12 packet (sized): 64 sample bytes
        report[11] = 0x80 | 0x12;
        report[12] = SamplesPerReport;
        return report;
    }

    /// <summary>Fills the samples, counter, and CRC. Kept separate for testing.</summary>
    internal static void FillReport(byte[] report, ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        for (int i = 0; i < FramesPerReport; i++)
        {
            report[SamplesOffset + 2 * i] = ToS8(left[i]);
            report[SamplesOffset + 2 * i + 1] = ToS8(right[i]);
        }
        report[CounterOffset]++;
        uint crc = Crc32.ComputeBluetoothOutput(report.AsSpan(0, CrcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(CrcOffset, 4), crc);
    }

    private static byte ToS8(float v)
    {
        int s = (int)MathF.Round(MathX.Clamp(v, -1f, 1f) * 127f);
        return unchecked((byte)(sbyte)s);
    }

    public void Start()
    {
        _running = true;
        _thread.Start();
    }

    private void Loop()
    {
        bool timerSet = false;
        try
        {
            timerSet = TimeBeginPeriod(1) == 0;
        }
        catch
        {
            // winmm is unavailable; use a coarser timer
        }

        double period = SamplesPerReport / (SampleRate * 2.0); // 10.667 ms
        var clock = Stopwatch.StartNew();
        double next = 0;
        int consecutiveErrors = 0;

        try
        {
            while (_running)
            {
                _source.Render(_left, _right);
                FillReport(_report, _left, _right);

                try
                {
                    _stream.Write(_report);
                    Interlocked.Increment(ref _sent);
                    consecutiveErrors = 0;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
                {
                    Interlocked.Increment(ref _errors);
                    if (++consecutiveErrors == 1) Log.Warn($"BT: write error ({ex.Message})");
                    if (consecutiveErrors > 300)
                    {
                        Log.Error("BT: the controller is not responding; stopping output. Reconnect the DualSense and restart the application.");
                        break;
                    }
                }

                next += period;
                double now = clock.Elapsed.TotalSeconds;
                if (now - next > 0.1) next = now; // far behind (sleep or freeze); do not try to catch up
                while (_running)
                {
                    double remaining = next - clock.Elapsed.TotalSeconds;
                    if (remaining <= 0) break;
                    if (remaining > 0.002) Thread.Sleep(1);
                    else Thread.SpinWait(100);
                }
            }
        }
        finally
        {
            if (timerSet)
            {
                try { TimeEndPeriod(1); } catch { /* ignore */ }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join(500);
        _stream.Dispose();
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}

/// <summary>CRC-32 for DualSense Bluetooth output reports: standard CRC-32 with a 0xA2 prefix.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint ComputeBluetoothOutput(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        crc = Table[(crc ^ 0xA2) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}

/// <summary>Finds a DualSense among HID devices.</summary>
public static class DualSenseHid
{
    public const int SonyVendorId = 0x054C;
    public static readonly int[] ProductIds = { 0x0CE6 /* DualSense */, 0x0DF2 /* DualSense Edge */ };

    public sealed record Info(HidDevice Device, bool IsBluetooth, string Name, int MaxInput, int MaxOutput);

    public static List<Info> Find()
    {
        var result = new List<Info>();
        foreach (int pid in ProductIds)
        {
            IEnumerable<HidDevice> devices;
            try
            {
                devices = DeviceList.Local.GetHidDevices(SonyVendorId, pid).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var device in devices)
            {
                int maxIn = SafeLength(device.GetMaxInputReportLength);
                int maxOut = SafeLength(device.GetMaxOutputReportLength);
                if (maxIn <= 0) continue;
                // USB: 64-byte input report; Bluetooth: 78 bytes (report 0x31)
                bool bluetooth = maxIn >= 78;
                result.Add(new Info(device, bluetooth, SafeProductName(device), maxIn, maxOut));
            }
        }
        return result;
    }

    public static string SafeProductName(HidDevice device)
    {
        try { return device.GetProductName(); } catch { return "DualSense"; }
    }

    private static int SafeLength(Func<int> getter)
    {
        try { return getter(); } catch { return -1; }
    }
}
