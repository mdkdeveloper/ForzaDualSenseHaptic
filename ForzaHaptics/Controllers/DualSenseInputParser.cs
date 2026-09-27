namespace ForzaHaptics.Controllers;

/// <summary>A physical controller report normalized to XInput axes and button masks.</summary>
public sealed record ControllerInputState(string DeviceId, long Timestamp,
    short LX, short LY, short RX, short RY, byte LT, byte RT, ushort Buttons);

public static class DualSenseInputParser
{
    // Full and simple layouts follow SDL's PS5StatePacket_t and PS5SimpleStatePacket_t:
    // https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c
    public static bool TryParse(ReadOnlySpan<byte> report, ControllerTransport transport,
        string deviceId, long timestamp, out ControllerInputState? input)
    {
        input = null;
        bool simple = transport == ControllerTransport.Bluetooth &&
            report.Length is 10 or 78 && report[0] == 0x01;
        if (!simple && !DualSenseBatteryParser.TryParse(report, transport, out _, out _)) return false;
        int common = transport == ControllerTransport.Bluetooth && !simple ? 2 : 1;
        int buttonOffset = common + (simple ? 4 : 7);
        int triggerOffset = common + (simple ? 7 : 4);
        byte face = report[buttonOffset], shoulder = report[buttonOffset + 1];
        ushort buttons = (face & 15) switch
        {
            0 => 0x0001, 1 => 0x0009, 2 => 0x0008, 3 => 0x000A,
            4 => 0x0002, 5 => 0x0006, 6 => 0x0004, 7 => 0x0005, _ => 0,
        };
        if ((face & 0x10) != 0) buttons |= 0x4000; // Square -> X
        if ((face & 0x20) != 0) buttons |= 0x1000; // Cross -> A
        if ((face & 0x40) != 0) buttons |= 0x2000; // Circle -> B
        if ((face & 0x80) != 0) buttons |= 0x8000; // Triangle -> Y
        if ((shoulder & 0x01) != 0) buttons |= 0x0100;
        if ((shoulder & 0x02) != 0) buttons |= 0x0200;
        if ((shoulder & 0x10) != 0) buttons |= 0x0020; // Create -> Back
        if ((shoulder & 0x20) != 0) buttons |= 0x0010; // Options -> Start
        if ((shoulder & 0x40) != 0) buttons |= 0x0040;
        if ((shoulder & 0x80) != 0) buttons |= 0x0080;
        if ((report[buttonOffset + 2] & 0x01) != 0) buttons |= 0x0400; // PS -> Guide
        input = new ControllerInputState(deviceId, timestamp,
            Axis(report[common], false), Axis(report[common + 1], true),
            Axis(report[common + 2], false), Axis(report[common + 3], true),
            report[triggerOffset], report[triggerOffset + 1], buttons);
        return true;
    }

    private static short Axis(byte value, bool invert)
    {
        int delta = value - 128;
        int result = invert
            ? delta < 0 ? -delta * 32767 / 128 : -delta * 32768 / 127
            : delta < 0 ? delta * 32768 / 128 : delta * 32767 / 127;
        return (short)result;
    }
}
