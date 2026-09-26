namespace ForzaHaptics.Triggers;

/// <summary>DualSense zone effects. Physical position selects the feedback zone in controller firmware.</summary>
public readonly record struct TriggerEffect(byte Mode, ulong ParamsLo, ushort ParamsHi)
{
    public const byte ModeOff = 0x05;
    public const byte ModeFeedback = 0x21;
    public const byte ModeVibration = 0x26;
    public static readonly TriggerEffect Off = new(ModeOff, 0, 0);

    public static TriggerEffect Feedback(int strength, int startZone = 1)
    {
        Span<float> zones = stackalloc float[10];
        zones.Clear();
        for (int i = Math.Clamp(startZone, 0, 9); i < 10; i++) zones[i] = strength;
        return Feedback(zones);
    }

    public static TriggerEffect Feedback(ReadOnlySpan<float> zones) => Encode(ModeFeedback, zones, 0);

    public static TriggerEffect Vibration(int frequency, int amplitude, int startZone = 1)
    {
        if (frequency <= 0) return Off;
        Span<float> zones = stackalloc float[10];
        zones.Clear();
        for (int i = Math.Clamp(startZone, 0, 9); i < 10; i++) zones[i] = amplitude;
        return Encode(ModeVibration, zones, Math.Clamp(frequency, 1, 255));
    }

    private static TriggerEffect Encode(byte mode, ReadOnlySpan<float> zones, int frequency)
    {
        if (zones.Length != 10) throw new ArgumentException("Exactly ten trigger zones are required.", nameof(zones));
        uint active = 0, levels = 0;
        for (int i = 0; i < 10; i++)
        {
            if (!float.IsFinite(zones[i])) return Off;
            int level = (int)MathF.Round(Math.Clamp(zones[i], 0, 8));
            if (level == 0) continue;
            active |= 1u << i;
            levels |= (uint)(level - 1) << (i * 3);
        }
        return active == 0 ? Off : new(mode, active | ((ulong)levels << 16), (ushort)frequency);
    }

    public int GetZoneStrength(int zone)
    {
        if (zone is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(zone));
        return (ParamsLo & (1UL << zone)) == 0 ? 0 : (int)((ParamsLo >> (16 + zone * 3)) & 7) + 1;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < 11) throw new ArgumentException("An effect needs 11 bytes.", nameof(destination));
        destination[0] = Mode;
        for (int i = 0; i < 8; i++) destination[i + 1] = (byte)(ParamsLo >> (8 * i));
        destination[9] = (byte)ParamsHi;
        destination[10] = (byte)(ParamsHi >> 8);
    }

    public override string ToString() => Mode switch
    {
        ModeOff => "off",
        ModeFeedback => $"curve [{string.Join(",", Enumerable.Range(0, 10).Select(GetZoneStrength))}]/8",
        ModeVibration => $"vibration {Strength}/8, {ParamsHi & 255} Hz",
        _ => $"unsupported 0x{Mode:X2}",
    };

    internal int Strength => Enumerable.Range(0, 10).Max(GetZoneStrength);
}
