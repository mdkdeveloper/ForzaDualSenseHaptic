using System.Buffers.Binary;
using ForzaHaptics.Controllers;
using HidSharp;

namespace ForzaHaptics.Emulation;

internal interface IXboxRumbleOutput : IDisposable
{
    void Write(byte largeMotor, byte smallMotor);
}

/// <summary>Legacy DualSense rumble; requires no USB audio endpoint. Owned exclusively while telemetry is stopped.</summary>
internal sealed class DualSenseRumbleOutput : IXboxRumbleOutput
{
    private readonly HidStream _stream;
    private readonly bool _bluetooth;
    private byte _sequence;
    private bool _disposed;

    public DualSenseRumbleOutput(ControllerSnapshot snapshot)
    {
        var device = DeviceList.Local.GetHidDevices().FirstOrDefault(d => d.DevicePath == snapshot.DeviceId)
            ?? throw new IOException("The DualSense rumble device is no longer available.");
        if (!device.TryOpen(out HidStream stream)) throw new IOException("Could not open DualSense for Xbox vibration.");
        _stream = stream;
        try { _stream.WriteTimeout = 200; }
        catch { _stream.Dispose(); throw; }
        _bluetooth = snapshot.Transport == ControllerTransport.Bluetooth;
    }

    // Protocol and legacy amplitude scaling follow SDL's HIDAPI PS5 driver.
    // https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c
    internal static byte[] BuildReport(bool bluetooth, byte largeMotor, byte smallMotor, byte sequence = 0,
        bool restoreAudio = false)
    {
        var report = new byte[bluetooth ? 78 : 48];
        int common = bluetooth ? 3 : 1;
        report[0] = bluetooth ? (byte)0x31 : (byte)0x02;
        if (bluetooth) { report[1] = (byte)((sequence & 15) << 4); report[2] = 0x10; }
        // Compatibility rumble and haptics-select only. No trigger, LED or audio-volume valid bits.
        report[common] = restoreAudio ? (byte)0 : (byte)0x03;
        report[common + 2] = restoreAudio ? (byte)0 : (byte)(smallMotor >> 1);
        report[common + 3] = restoreAudio ? (byte)0 : (byte)(largeMotor >> 1);
        if (bluetooth)
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), DualSenseBatteryParser.ComputeCrc(0xA2, report.AsSpan(0, 74)));
        return report;
    }

    public void Write(byte largeMotor, byte smallMotor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Write(BuildReport(_bluetooth, largeMotor, smallMotor, _sequence++));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Explicit zero before releasing the rumble mode. Reset cannot race a telemetry writer.
            try { _stream.Write(BuildReport(_bluetooth, 0, 0, _sequence++)); }
            finally { _stream.Write(BuildReport(_bluetooth, 0, 0, _sequence++, restoreAudio: true)); }
        }
        finally { _stream.Dispose(); }
    }
}
