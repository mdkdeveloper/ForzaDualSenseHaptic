using System.Buffers.Binary;
using System.Numerics;

namespace ForzaHaptics.Telemetry;

/// <summary>Values for four wheels. Indices: 0 = FL, 1 = FR, 2 = RL, 3 = RR.</summary>
public struct Wheels
{
    public float FL, FR, RL, RR;

    public Wheels(float fl, float fr, float rl, float rr)
    {
        FL = fl; FR = fr; RL = rl; RR = rr;
    }

    public float this[int index]
    {
        readonly get => index switch { 0 => FL, 1 => FR, 2 => RL, _ => RR };
        set
        {
            switch (index)
            {
                case 0: FL = value; break;
                case 1: FR = value; break;
                case 2: RL = value; break;
                default: RR = value; break;
            }
        }
    }

    /// <summary>Maximum absolute value among the wheels on one side (0 = left, 1 = right).</summary>
    public readonly float MaxAbs(int side) =>
        side == 0 ? MathF.Max(MathF.Abs(FL), MathF.Abs(RL)) : MathF.Max(MathF.Abs(FR), MathF.Abs(RR));

    /// <summary>Average for one side (0 = left, 1 = right).</summary>
    public readonly float Avg(int side) => side == 0 ? (FL + RL) * 0.5f : (FR + RR) * 0.5f;

    public readonly bool Any(int side) => side == 0 ? (FL != 0f || RL != 0f) : (FR != 0f || RR != 0f);

    public readonly float Max() => MathF.Max(MathF.Max(FL, FR), MathF.Max(RL, RR));
    public readonly bool IsFinite => float.IsFinite(FL) && float.IsFinite(FR) && float.IsFinite(RL) && float.IsFinite(RR);
}

/// <summary>
/// Byte offsets of fields in a Forza Horizon 6 "Data Out" packet (324 bytes, little-endian).
/// Field order follows the official FH6 documentation.
/// </summary>
public static class ForzaOffsets
{
    public const int IsRaceOn = 0;
    public const int TimestampMs = 4;
    public const int EngineMaxRpm = 8;
    public const int EngineIdleRpm = 12;
    public const int CurrentEngineRpm = 16;
    public const int AccelerationX = 20;            // X, Y, Z
    public const int VelocityX = 32;                // X, Y, Z
    public const int AngularVelocityX = 44;         // X, Y, Z
    public const int Yaw = 56;
    public const int Pitch = 60;
    public const int Roll = 64;
    public const int NormalizedSuspensionTravelFL = 68;
    public const int TireSlipRatioFL = 84;
    public const int WheelRotationSpeedFL = 100;
    public const int WheelOnRumbleStripFL = 116;    // S32
    public const int WheelInPuddleFL = 132;         // S32 (F32 depth in FM)
    public const int SurfaceRumbleFL = 148;
    public const int TireSlipAngleFL = 164;
    public const int TireCombinedSlipFL = 180;
    public const int SuspensionTravelMetersFL = 196;
    public const int CarOrdinal = 212;
    public const int CarClass = 216;
    public const int CarPerformanceIndex = 220;
    public const int DrivetrainType = 224;
    public const int NumCylinders = 228;
    public const int CarGroup = 232;                // U32, Horizon only
    public const int SmashableVelDiff = 236;        // Horizon only
    public const int SmashableMass = 240;           // Horizon only
    public const int PositionX = 244;               // X, Y, Z
    public const int Speed = 256;
    public const int Power = 260;
    public const int Torque = 264;
    public const int TireTempFL = 268;
    public const int Boost = 284;
    public const int Fuel = 288;
    public const int DistanceTraveled = 292;
    public const int BestLap = 296;
    public const int LastLap = 300;
    public const int CurrentLap = 304;
    public const int CurrentRaceTime = 308;
    public const int LapNumber = 312;               // U16
    public const int RacePosition = 314;            // U8
    public const int Accel = 315;                   // U8 0..255
    public const int Brake = 316;
    public const int Clutch = 317;
    public const int HandBrake = 318;
    public const int Gear = 319;
    public const int Steer = 320;                   // S8
    public const int NormalizedDrivingLine = 321;
    public const int NormalizedAIBrakeDifference = 322;
}

/// <summary>One frame of Forza Horizon 6 telemetry.</summary>
public sealed class ForzaPacket
{
    public const int Size = 324;
    public const int MinSize = 323;

    public bool IsRaceOn;
    public uint TimestampMs;
    public float EngineMaxRpm, EngineIdleRpm, CurrentEngineRpm;

    /// <summary>Acceleration in vehicle coordinates, m/s². X is right, Y is up, Z is forward.</summary>
    public Vector3 Acceleration;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    public float Yaw, Pitch, Roll;

    /// <summary>0 = suspension fully extended, 1 = fully compressed.</summary>
    public Wheels NormalizedSuspensionTravel;
    /// <summary>0 = full grip, |x| > 1 = loss of longitudinal grip.</summary>
    public Wheels TireSlipRatio;
    public Wheels WheelRotationSpeed;
    /// <summary>1 when the wheel is on a rumble strip.</summary>
    public Wheels WheelOnRumbleStrip;
    /// <summary>0..1 indicating whether the wheel is in a puddle.</summary>
    public Wheels WheelInPuddle;
    /// <summary>Dimensionless surface vibration supplied by the game to force feedback.</summary>
    public Wheels SurfaceRumble;
    public Wheels TireSlipAngle;
    /// <summary>Combined tire slip: > 1 means loss of grip.</summary>
    public Wheels TireCombinedSlip;
    public Wheels SuspensionTravelMeters;

    public int CarOrdinal, CarClass, CarPerformanceIndex, DrivetrainType, NumCylinders;
    public uint CarGroup;
    public float SmashableVelDiff, SmashableMass;

    public Vector3 Position;
    /// <summary>Speed in m/s.</summary>
    public float Speed;
    public float Power, Torque;
    public Wheels TireTemp;
    public float Boost, Fuel, DistanceTraveled, BestLap, LastLap, CurrentLap, CurrentRaceTime;
    public ushort LapNumber;
    public byte RacePosition, Accel, Brake, Clutch, HandBrake, Gear;
    public sbyte Steer, NormalizedDrivingLine, NormalizedAIBrakeDifference;

    public float SpeedKmh => Speed * 3.6f;
    public float Throttle01 => Accel / 255f;
    public float Brake01 => Brake / 255f;
    // All packet members are value types; detach the publication from callers that reuse a frame.
    internal ForzaPacket CopyForTriggers() => (ForzaPacket)MemberwiseClone();

    public static bool TryParse(ReadOnlySpan<byte> d, out ForzaPacket packet)
    {
        packet = new ForzaPacket();
        if (d.Length != MinSize && d.Length != Size) return false;

        var p = packet;
        p.IsRaceOn = I32(d, ForzaOffsets.IsRaceOn) != 0;
        p.TimestampMs = U32(d, ForzaOffsets.TimestampMs);
        p.EngineMaxRpm = F32(d, ForzaOffsets.EngineMaxRpm);
        p.EngineIdleRpm = F32(d, ForzaOffsets.EngineIdleRpm);
        p.CurrentEngineRpm = F32(d, ForzaOffsets.CurrentEngineRpm);
        p.Acceleration = V3(d, ForzaOffsets.AccelerationX);
        p.Velocity = V3(d, ForzaOffsets.VelocityX);
        p.AngularVelocity = V3(d, ForzaOffsets.AngularVelocityX);
        p.Yaw = F32(d, ForzaOffsets.Yaw);
        p.Pitch = F32(d, ForzaOffsets.Pitch);
        p.Roll = F32(d, ForzaOffsets.Roll);
        p.NormalizedSuspensionTravel = FW(d, ForzaOffsets.NormalizedSuspensionTravelFL);
        p.TireSlipRatio = FW(d, ForzaOffsets.TireSlipRatioFL);
        p.WheelRotationSpeed = FW(d, ForzaOffsets.WheelRotationSpeedFL);
        p.WheelOnRumbleStrip = FlagWheels(d, ForzaOffsets.WheelOnRumbleStripFL);
        p.WheelInPuddle = PuddleWheels(d, ForzaOffsets.WheelInPuddleFL);
        p.SurfaceRumble = FW(d, ForzaOffsets.SurfaceRumbleFL);
        p.TireSlipAngle = FW(d, ForzaOffsets.TireSlipAngleFL);
        p.TireCombinedSlip = FW(d, ForzaOffsets.TireCombinedSlipFL);
        p.SuspensionTravelMeters = FW(d, ForzaOffsets.SuspensionTravelMetersFL);
        p.CarOrdinal = I32(d, ForzaOffsets.CarOrdinal);
        p.CarClass = I32(d, ForzaOffsets.CarClass);
        p.CarPerformanceIndex = I32(d, ForzaOffsets.CarPerformanceIndex);
        p.DrivetrainType = I32(d, ForzaOffsets.DrivetrainType);
        p.NumCylinders = I32(d, ForzaOffsets.NumCylinders);
        p.CarGroup = U32(d, ForzaOffsets.CarGroup);
        p.SmashableVelDiff = F32(d, ForzaOffsets.SmashableVelDiff);
        p.SmashableMass = F32(d, ForzaOffsets.SmashableMass);
        p.Position = V3(d, ForzaOffsets.PositionX);
        p.Speed = F32(d, ForzaOffsets.Speed);
        p.Power = F32(d, ForzaOffsets.Power);
        p.Torque = F32(d, ForzaOffsets.Torque);
        p.TireTemp = FW(d, ForzaOffsets.TireTempFL);
        p.Boost = F32(d, ForzaOffsets.Boost);
        p.Fuel = F32(d, ForzaOffsets.Fuel);
        p.DistanceTraveled = F32(d, ForzaOffsets.DistanceTraveled);
        p.BestLap = F32(d, ForzaOffsets.BestLap);
        p.LastLap = F32(d, ForzaOffsets.LastLap);
        p.CurrentLap = F32(d, ForzaOffsets.CurrentLap);
        p.CurrentRaceTime = F32(d, ForzaOffsets.CurrentRaceTime);
        p.LapNumber = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(ForzaOffsets.LapNumber, 2));
        p.RacePosition = d[ForzaOffsets.RacePosition];
        p.Accel = d[ForzaOffsets.Accel];
        p.Brake = d[ForzaOffsets.Brake];
        p.Clutch = d[ForzaOffsets.Clutch];
        p.HandBrake = d[ForzaOffsets.HandBrake];
        p.Gear = d[ForzaOffsets.Gear];
        p.Steer = unchecked((sbyte)d[ForzaOffsets.Steer]);
        p.NormalizedDrivingLine = unchecked((sbyte)d[ForzaOffsets.NormalizedDrivingLine]);
        p.NormalizedAIBrakeDifference = unchecked((sbyte)d[ForzaOffsets.NormalizedAIBrakeDifference]);

        // Reject garbage data: NaN/Infinity in key fields.
        return p.HasFiniteFeedbackValues;
    }

    /// <summary>Validate every float consumed by feedback or status, including direct simulator input.</summary>
    public bool HasFiniteFeedbackValues =>
        float.IsFinite(Speed) && float.IsFinite(SpeedKmh) && float.IsFinite(EngineMaxRpm)
        && float.IsFinite(EngineIdleRpm) && float.IsFinite(CurrentEngineRpm)
        && float.IsFinite(Acceleration.X) && float.IsFinite(Acceleration.Y) && float.IsFinite(Acceleration.Z)
        && float.IsFinite(Velocity.Z)
        && NormalizedSuspensionTravel.IsFinite && TireSlipRatio.IsFinite && TireSlipAngle.IsFinite && WheelOnRumbleStrip.IsFinite
        && WheelInPuddle.IsFinite && SurfaceRumble.IsFinite && TireCombinedSlip.IsFinite
        && SuspensionTravelMeters.IsFinite && float.IsFinite(SmashableVelDiff) && float.IsFinite(Boost);

    /// <summary>Serializes to a 324-byte packet for the simulator and tests.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        var d = bytes.AsSpan();
        WI32(d, ForzaOffsets.IsRaceOn, IsRaceOn ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(ForzaOffsets.TimestampMs, 4), TimestampMs);
        WF32(d, ForzaOffsets.EngineMaxRpm, EngineMaxRpm);
        WF32(d, ForzaOffsets.EngineIdleRpm, EngineIdleRpm);
        WF32(d, ForzaOffsets.CurrentEngineRpm, CurrentEngineRpm);
        WV3(d, ForzaOffsets.AccelerationX, Acceleration);
        WV3(d, ForzaOffsets.VelocityX, Velocity);
        WV3(d, ForzaOffsets.AngularVelocityX, AngularVelocity);
        WF32(d, ForzaOffsets.Yaw, Yaw);
        WF32(d, ForzaOffsets.Pitch, Pitch);
        WF32(d, ForzaOffsets.Roll, Roll);
        WFW(d, ForzaOffsets.NormalizedSuspensionTravelFL, NormalizedSuspensionTravel);
        WFW(d, ForzaOffsets.TireSlipRatioFL, TireSlipRatio);
        WFW(d, ForzaOffsets.WheelRotationSpeedFL, WheelRotationSpeed);
        WIW(d, ForzaOffsets.WheelOnRumbleStripFL, WheelOnRumbleStrip);
        WIW(d, ForzaOffsets.WheelInPuddleFL, WheelInPuddle);
        WFW(d, ForzaOffsets.SurfaceRumbleFL, SurfaceRumble);
        WFW(d, ForzaOffsets.TireSlipAngleFL, TireSlipAngle);
        WFW(d, ForzaOffsets.TireCombinedSlipFL, TireCombinedSlip);
        WFW(d, ForzaOffsets.SuspensionTravelMetersFL, SuspensionTravelMeters);
        WI32(d, ForzaOffsets.CarOrdinal, CarOrdinal);
        WI32(d, ForzaOffsets.CarClass, CarClass);
        WI32(d, ForzaOffsets.CarPerformanceIndex, CarPerformanceIndex);
        WI32(d, ForzaOffsets.DrivetrainType, DrivetrainType);
        WI32(d, ForzaOffsets.NumCylinders, NumCylinders);
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(ForzaOffsets.CarGroup, 4), CarGroup);
        WF32(d, ForzaOffsets.SmashableVelDiff, SmashableVelDiff);
        WF32(d, ForzaOffsets.SmashableMass, SmashableMass);
        WV3(d, ForzaOffsets.PositionX, Position);
        WF32(d, ForzaOffsets.Speed, Speed);
        WF32(d, ForzaOffsets.Power, Power);
        WF32(d, ForzaOffsets.Torque, Torque);
        WFW(d, ForzaOffsets.TireTempFL, TireTemp);
        WF32(d, ForzaOffsets.Boost, Boost);
        WF32(d, ForzaOffsets.Fuel, Fuel);
        WF32(d, ForzaOffsets.DistanceTraveled, DistanceTraveled);
        WF32(d, ForzaOffsets.BestLap, BestLap);
        WF32(d, ForzaOffsets.LastLap, LastLap);
        WF32(d, ForzaOffsets.CurrentLap, CurrentLap);
        WF32(d, ForzaOffsets.CurrentRaceTime, CurrentRaceTime);
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(ForzaOffsets.LapNumber, 2), LapNumber);
        d[ForzaOffsets.RacePosition] = RacePosition;
        d[ForzaOffsets.Accel] = Accel;
        d[ForzaOffsets.Brake] = Brake;
        d[ForzaOffsets.Clutch] = Clutch;
        d[ForzaOffsets.HandBrake] = HandBrake;
        d[ForzaOffsets.Gear] = Gear;
        d[ForzaOffsets.Steer] = unchecked((byte)Steer);
        d[ForzaOffsets.NormalizedDrivingLine] = unchecked((byte)NormalizedDrivingLine);
        d[ForzaOffsets.NormalizedAIBrakeDifference] = unchecked((byte)NormalizedAIBrakeDifference);
        return bytes;
    }

    // ---- reading ----
    private static float F32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d.Slice(o, 4));
    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.Slice(o, 4));
    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o, 4));
    private static Vector3 V3(ReadOnlySpan<byte> d, int o) => new(F32(d, o), F32(d, o + 4), F32(d, o + 8));
    private static Wheels FW(ReadOnlySpan<byte> d, int o) => new(F32(d, o), F32(d, o + 4), F32(d, o + 8), F32(d, o + 12));

    private static Wheels FlagWheels(ReadOnlySpan<byte> d, int o) =>
        new(I32(d, o) != 0 ? 1f : 0f, I32(d, o + 4) != 0 ? 1f : 0f, I32(d, o + 8) != 0 ? 1f : 0f, I32(d, o + 12) != 0 ? 1f : 0f);

    private static Wheels PuddleWheels(ReadOnlySpan<byte> d, int o) =>
        new(PuddleValue(I32(d, o)), PuddleValue(I32(d, o + 4)), PuddleValue(I32(d, o + 8)), PuddleValue(I32(d, o + 12)));

    /// <summary>
    /// FH6 documentation specifies S32, while Forza Motorsport uses F32 (depth 0..1).
    /// Accept both variants: a small integer is a flag; otherwise interpret it as a float.
    /// </summary>
    private static float PuddleValue(int raw)
    {
        if (raw == 0) return 0f;
        if (raw > 0 && raw < 0x00800000) return 1f; // integer (0/1/2...), not a normalized float
        float f = BitConverter.Int32BitsToSingle(raw);
        if (!float.IsFinite(f) || f < 1e-4f) return 0f;
        return f > 1f ? 1f : f;
    }

    // ---- writing ----
    private static void WF32(Span<byte> d, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.Slice(o, 4), v);
    private static void WI32(Span<byte> d, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.Slice(o, 4), v);

    private static void WV3(Span<byte> d, int o, Vector3 v)
    {
        WF32(d, o, v.X); WF32(d, o + 4, v.Y); WF32(d, o + 8, v.Z);
    }

    private static void WFW(Span<byte> d, int o, Wheels w)
    {
        WF32(d, o, w.FL); WF32(d, o + 4, w.FR); WF32(d, o + 8, w.RL); WF32(d, o + 12, w.RR);
    }

    private static void WIW(Span<byte> d, int o, Wheels w)
    {
        WI32(d, o, w.FL > 0.5f ? 1 : 0);
        WI32(d, o + 4, w.FR > 0.5f ? 1 : 0);
        WI32(d, o + 8, w.RL > 0.5f ? 1 : 0);
        WI32(d, o + 12, w.RR > 0.5f ? 1 : 0);
    }
}
