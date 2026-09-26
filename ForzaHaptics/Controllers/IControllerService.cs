using System.Buffers.Binary;

namespace ForzaHaptics.Controllers;

public enum ControllerTransport { None, Usb, Bluetooth }

public sealed record ControllerSnapshot
{
    public static readonly ControllerSnapshot Disconnected = new();
    public string? DeviceId { get; init; }
    public string Name { get; init; } = "DualSense";
    public bool IsConnected { get; init; }
    public ControllerTransport Transport { get; init; }
    public int? BatteryPercent { get; init; }
    public bool IsCharging { get; init; }
    public bool CanDisconnect { get; init; }
}

public interface IControllerService : IDisposable
{
    ControllerSnapshot Snapshot { get; }
    Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default);
}

/// <summary>DualSense common input status byte, per Linux hid-playstation. Capacity is a 10% bucket midpoint.</summary>
public static class DualSenseBatteryParser
{
    public static bool TryParse(ReadOnlySpan<byte> report, ControllerTransport transport, out int? percent, out bool charging)
    {
        percent = null;
        charging = false;
        int offset;
        if (transport == ControllerTransport.Usb && report.Length == 64 && report[0] == 0x01)
            offset = 53;
        else if (transport == ControllerTransport.Bluetooth && report.Length == 78 && report[0] == 0x31)
        {
            if (ComputeCrc(0xA1, report[..74]) != BinaryPrimitives.ReadUInt32LittleEndian(report[74..])) return false;
            offset = 54;
        }
        else return false;

        int level = report[offset] & 0x0F;
        int status = report[offset] >> 4;
        // 0: discharging, 1: charging, 2: full. Other values report battery/charging errors.
        if (status == 2) percent = 100;
        else if (status <= 1 && level <= 10)
        {
            percent = Math.Min(level * 10 + 5, 100);
            charging = status == 1;
        }
        return true;
    }

    public static uint ComputeCrc(byte seed, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        Update(seed);
        foreach (byte value in data) Update(value);
        return ~crc;
        void Update(byte value)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
    }
}
