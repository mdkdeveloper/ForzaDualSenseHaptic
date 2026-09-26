namespace ForzaHaptics.Triggers;

/// <summary>
/// DualSense adaptive trigger effect: a mode byte plus up to 10 parameter bytes in the HID output report.
/// Encoding follows HorizonHaptics (dualsense/triggers.py), based on reverse engineering from
/// https://github.com/nondebug/dualsense
/// </summary>
public readonly record struct TriggerEffect(byte Mode, ulong ParamsLo, ushort ParamsHi)
{
    public const byte ModeOff = 0x05;
    public const byte ModeRigid = 0x01;
    public const byte ModeVibration = 0x06;
    public const byte ModeFeedback = 0x21;  // 10 zones, each with strength 0..8.
    public const byte ModePulseAB = 0x26;   // Zones plus a rhythmic pulse.

    public static readonly TriggerEffect Off = new(ModeOff, 0, 0);

    public static TriggerEffect Rigid(int force) => Create(ModeRigid, 0, Byte(force));

    public static TriggerEffect Vibration(int freq, int amp) => Create(ModeVibration, Byte(freq), Byte(amp));

    /// <summary>Uniform resistance from 0..8 across the full trigger travel.</summary>
    public static TriggerEffect Feedback(int strength)
    {
        Span<int> zones = stackalloc int[10];
        zones.Fill(strength);
        return Create(ModeFeedback, PackZones(zones));
    }

    /// <summary>Lower zones vibrate at amp (1..8); upper wallZones form a wall at wallStrength.</summary>
    public static TriggerEffect VibrationWall(int amp, int freq, int wallZones, int wallStrength = 8)
    {
        int wall = Math.Clamp(wallZones, 1, 9);
        amp = Math.Clamp(amp, 1, 8);
        wallStrength = Math.Clamp(wallStrength, 1, 8);
        Span<int> zones = stackalloc int[10];
        for (int i = 0; i < 10; i++) zones[i] = i < 10 - wall ? amp : wallStrength;
        Span<byte> p = stackalloc byte[7];
        PackZones(zones).CopyTo(p);
        p[6] = Byte(freq);
        return Create(ModePulseAB, p);
    }

    /// <summary>
    /// Scales zone strength, resistance, and amplitude by k. Frequencies remain unchanged.
    /// A non-zero zone strength never drops below 1, so the effect does not disappear completely.
    /// </summary>
    public TriggerEffect Scale(float k)
    {
        if (k >= 1f || Mode == ModeOff) return this;
        Span<byte> p = stackalloc byte[10];
        for (int i = 0; i < 8; i++) p[i] = (byte)(ParamsLo >> (8 * i));
        p[8] = (byte)ParamsHi;
        p[9] = (byte)(ParamsHi >> 8);

        switch (Mode)
        {
            case ModeRigid:
            case ModeVibration:
                p[1] = Byte((int)MathF.Round(p[1] * k)); // Strength / amplitude.
                break;
            case ModeFeedback:
            case ModePulseAB:
            {
                uint active = (uint)(p[0] | p[1] << 8);
                uint strength = (uint)(p[2] | p[3] << 8 | p[4] << 16 | p[5] << 24);
                Span<int> zones = stackalloc int[10];
                for (int i = 0; i < 10; i++)
                {
                    if ((active & (1u << i)) == 0) continue;
                    int s = (int)((strength >> (3 * i)) & 7) + 1;
                    zones[i] = Math.Max(1, (int)MathF.Round(s * k));
                }
                PackZones(zones).CopyTo(p);
                break;
            }
        }
        return Create(Mode, (ReadOnlySpan<byte>)p);
    }

    public void WriteTo(Span<byte> dest)
    {
        dest[0] = Mode;
        for (int i = 0; i < 8; i++) dest[1 + i] = (byte)(ParamsLo >> (8 * i));
        dest[9] = (byte)ParamsHi;
        dest[10] = (byte)(ParamsHi >> 8);
    }

    public override string ToString() => Mode switch
    {
        ModeOff => "off",
        ModeRigid => $"rigid {(ParamsLo >> 8) & 0xFF}",
        ModeVibration => $"vib {ParamsLo & 0xFF}/{(ParamsLo >> 8) & 0xFF}",
        ModeFeedback => $"resistance {FirstZoneStrength()}",
        ModePulseAB => $"ABS {(ParamsLo >> 48) & 0xFF} Hz",
        _ => $"0x{Mode:X2}",
    };

    private int FirstZoneStrength() => (ParamsLo & 1) == 0 ? 0 : (int)((ParamsLo >> 16) & 7) + 1;

    private static byte Byte(int v) => (byte)Math.Clamp(v, 0, 255);

    private static TriggerEffect Create(byte mode, params byte[] p) => Create(mode, (ReadOnlySpan<byte>)p);

    private static TriggerEffect Create(byte mode, ReadOnlySpan<byte> p)
    {
        ulong lo = 0;
        ushort hi = 0;
        for (int i = 0; i < p.Length && i < 10; i++)
        {
            if (i < 8) lo |= (ulong)p[i] << (8 * i);
            else hi |= (ushort)(p[i] << (8 * (i - 8)));
        }
        return new TriggerEffect(mode, lo, hi);
    }

    /// <summary>Active-zone mask (2 bytes) plus 3 strength bits per zone (4 bytes).</summary>
    private static byte[] PackZones(ReadOnlySpan<int> zones)
    {
        uint active = 0, strength = 0;
        for (int i = 0; i < zones.Length && i < 10; i++)
        {
            int s = Math.Clamp(zones[i], 0, 8);
            if (s == 0) continue;
            active |= 1u << i;
            strength |= (uint)(s - 1) << (3 * i);
        }
        return new[]
        {
            (byte)active, (byte)(active >> 8),
            (byte)strength, (byte)(strength >> 8), (byte)(strength >> 16), (byte)(strength >> 24),
        };
    }
}
