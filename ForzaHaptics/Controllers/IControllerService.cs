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
    public ControllerTriggerFeedback? TriggerFeedback { get; init; }
    public long LastInputTick { get; init; }
    public byte LeftTrigger { get; init; }
    public byte RightTrigger { get; init; }
    public bool IsInputFreshAt(long nowTick) => IsConnected && LastInputTick > 0 &&
        nowTick - LastInputTick is >= 0 and <= 300;
    public bool IsInputFresh => IsInputFreshAt(Environment.TickCount64);
    public string PhysicalTriggerText => IsInputFresh
        ? $"physical L2 {LeftTrigger}/255 / R2 {RightTrigger}/255" : "physical L2/R2: unavailable";
    public string TriggerFeedbackText => !IsInputFresh || TriggerFeedback is null
        ? "controller reported: unavailable"
        : $"controller reported: {TriggerFeedback}";
}

public interface IControllerService : IDisposable
{
    ControllerSnapshot Snapshot { get; }
    event Action<ControllerInputState>? InputReceived { add { } remove { } }
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

/// <summary>Reported firmware mode and motor state; neither measured force nor acknowledgement of a particular write.</summary>
public sealed record ControllerTriggerFeedback(byte LeftEffect, byte RightEffect, byte LeftStatus, byte RightStatus)
{
    public override string ToString() => $"L2 {Describe(LeftEffect, LeftStatus)} / R2 {Describe(RightEffect, RightStatus)}";
    private static string Describe(byte effect, byte status)
    {
        string mode = effect switch { 0 => "off/other", 1 => "feedback", 2 => "weapon", 3 => "vibration", _ => $"unknown(0x{effect:X})" };
        return $"{mode} state=0x{status:X}";
    }
}

public static class DualSenseTriggerFeedbackParser
{
    // Common-input offsets from https://github.com/SpecialKO/XInput_HID/blob/master/dualsense.cpp
    // Trigger status: 41/42 high nibble. Active effect: 47, right low/left high nibble.
    public static bool TryParse(ReadOnlySpan<byte> report, ControllerTransport transport, out ControllerTriggerFeedback? feedback)
    {
        feedback = null;
        // Reuse strict length/ID/transport checks and the Bluetooth input CRC (USB has no report CRC).
        if (!DualSenseBatteryParser.TryParse(report, transport, out _, out _)) return false;
        int common = transport == ControllerTransport.Bluetooth ? 2 : 1;
        byte effects = report[common + 47];
        feedback = new ControllerTriggerFeedback((byte)(effects >> 4), (byte)(effects & 15),
            (byte)(report[common + 42] >> 4), (byte)(report[common + 41] >> 4));
        return true;
    }
}

/// <summary>Physical trigger travel from validated full input reports; never inferred from telemetry.</summary>
public static class DualSensePhysicalInputParser
{
    public static bool TryParse(ReadOnlySpan<byte> report, ControllerTransport transport,
        out byte leftTrigger, out byte rightTrigger)
    {
        leftTrigger = rightTrigger = 0;
        if (!DualSenseBatteryParser.TryParse(report, transport, out _, out _)) return false;
        int common = transport == ControllerTransport.Bluetooth ? 2 : 1;
        leftTrigger = report[common + 4];
        rightTrigger = report[common + 5];
        return true;
    }
}
