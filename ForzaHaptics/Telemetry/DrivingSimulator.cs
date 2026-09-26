using System.Numerics;
using ForzaHaptics.Util;

namespace ForzaHaptics.Telemetry;

/// <summary>
/// Synthetic drive (42-second cycle) for testing and tuning effects without the game:
/// acceleration with wheelspin, rumble strip, gravel, jump, oversteer, lockup, puddle,
/// fence and collision, revving to the limiter, cruising, and pause.
/// </summary>
public sealed class DrivingSimulator
{
    public const double Duration = 42.0;

    // (time, speed in km/h)
    private static readonly (double T, float V)[] SpeedKeys =
    {
        (0, 0), (8, 140), (11, 140), (12.5, 110), (17, 110), (20, 100), (24, 95), (25, 120),
        (28, 40), (30.4, 60), (30.8, 5), (31, 0), (36, 0), (39, 80), (42, 80),
    };

    private static readonly float[] GearTopKmh = { 0, 55, 90, 125, 160, 200, 260 };

    private readonly Random _rng = new(1234);
    private double _prevT = double.NaN;
    private float _prevSpeed;
    private float _pitch;
    private float _rpm = 850f;
    private float _distance;

    public static string DescribePhase(double time)
    {
        double t = time % Duration;
        if (t < 8) return "acceleration (wheelspin at launch, gear shift)";
        if (t < 11) return "left rumble strip, 140 km/h";
        if (t < 17) return "gravel";
        if (t < 20) return "jump and landing";
        if (t < 25) return "oversteer (rear wheels)";
        if (t < 28) return "braking with front-wheel lockup";
        if (t < 29.8) return "puddle on the right";
        if (t < 31) return "fence, then an impact from the left";
        if (t < 36) return "stationary: revving to the limiter";
        if (t < 41.5) return "cruising at 80 km/h";
        return "paused (IsRaceOn = 0)";
    }

    public ForzaPacket Sample(double time)
    {
        double t = time % Duration;
        if (double.IsNaN(_prevT) || t < _prevT)
        {
            _prevSpeed = SpeedAt(t) / 3.6f;
            _pitch = 0f;
            _rpm = 850f;
        }
        float dt = double.IsNaN(_prevT) ? 1f / 60f : (float)Math.Max(1e-3, t - _prevT);
        if (t < _prevT) dt = 1f / 60f;
        _prevT = t;

        float vKmh = SpeedAt(t);
        float v = vKmh / 3.6f;
        float accelZ = (v - _prevSpeed) / dt;
        _prevSpeed = v;
        _pitch += (accelZ - _pitch) * MathX.Clamp01(dt * 4f);
        _distance += v * dt;

        bool gravel = t >= 11 && t < 17;
        bool airborne = t >= 17.5 && t < 18.4;
        bool landing = t >= 18.4 && t < 18.9;
        bool drift = t >= 20 && t < 25;
        bool lockup = t >= 25.2 && t < 27.6;
        bool revPhase = t >= 31 && t < 36;

        var p = new ForzaPacket
        {
            IsRaceOn = t < 41.5,
            TimestampMs = (uint)(time * 1000.0),
            EngineMaxRpm = 8000f,
            EngineIdleRpm = 850f,
            CarOrdinal = 2352,
            CarClass = 5,
            CarPerformanceIndex = 800,
            DrivetrainType = 1,
            NumCylinders = 6,
            Speed = v,
            Velocity = new Vector3(0f, 0f, v),
            Position = new Vector3(0f, 0f, _distance),
            DistanceTraveled = _distance,
            Fuel = 0.8f,
            TireTemp = new Wheels(80, 80, 85, 85),
        };

        // --- pedals ---
        float throttle = accelZ > 0.3f ? MathX.Clamp01(0.4f + accelZ / 6f) : (vKmh > 1f ? 0.25f : 0f);
        float brake = accelZ < -2f ? MathX.Clamp01(-accelZ / 9f) : 0f;
        if (t >= 25 && t < 28) { throttle = 0f; brake = 1f; }
        if (drift) throttle = 0.8f;
        if (revPhase) { throttle = t >= 32 && t < 35.5 ? 1f : 0f; brake = 0f; }

        // --- gear and RPM ---
        int gear = 1;
        while (gear < GearTopKmh.Length - 1 && vKmh > GearTopKmh[gear] * 0.95f) gear++;
        float rpmTarget = vKmh < 3f
            ? 850f + throttle * 7300f
            : MathX.Clamp(1500f + 6300f * vKmh / GearTopKmh[gear], 850f, 8000f);
        _rpm += (rpmTarget - _rpm) * MathX.Clamp01(dt * 10f);
        if (revPhase && throttle > 0f && _rpm > 7850f)
            _rpm = Math.Sin(t * 2 * Math.PI * 12) > 0 ? 8000f : 7800f; // limiter
        p.CurrentEngineRpm = _rpm;
        p.Gear = (byte)gear;
        p.Accel = (byte)(throttle * 255f);
        p.Brake = (byte)(brake * 255f);
        p.Clutch = revPhase ? (byte)255 : (byte)0;
        p.Power = throttle * 300000f;
        p.Torque = throttle * 500f;

        // --- acceleration (m/s²) ---
        float accelX = 0.4f * Noise(t, 9, 1f);
        float accelY = 0f;
        if (drift) accelX += 6f + 2f * Noise(t, 10, 1.5f);
        if (t >= 30.40 && t < 30.42) accelX += 140f;      // impact from the left → pushes right (+X)
        else if (t >= 30.42 && t < 30.46) accelX += 50f;
        if (t >= 18.4 && t < 18.45) accelY = 40f;         // landing
        p.Acceleration = new Vector3(accelX, accelY, accelZ);
        p.AngularVelocity = new Vector3(0f, drift ? 0.6f : 0f, 0f);

        // --- suspension ---
        float roughness = gravel ? 0.012f : 0.002f;
        for (int w = 0; w < 4; w++)
        {
            float travel = 0.08f + roughness * Noise(t, w, gravel ? 3f : 1f);
            if (gravel) travel += (float)(_rng.NextDouble() - 0.5) * 0.008f;
            travel += (w < 2 ? -_pitch : _pitch) * 0.0015f; // brake dive
            if (airborne) travel = 0f;
            if (landing) travel = 0.16f - 0.08f * MathX.SmoothStep(0f, 1f, (float)((t - 18.4) / 0.5));
            p.SuspensionTravelMeters[w] = travel;
            p.NormalizedSuspensionTravel[w] = MathX.Clamp01(travel / 0.16f);
        }

        // --- surface ---
        float surface = gravel ? 0.45f + 0.2f * Noise(t, 6, 4f) : 0.05f + 0.02f * Noise(t, 5, 2f);
        if (airborne) surface = 0f;
        p.SurfaceRumble = new Wheels(surface, surface, surface, surface);
        if (t >= 8.2 && t < 10.8)
        {
            p.WheelOnRumbleStrip = new Wheels(1, 0, 1, 0);
            p.SurfaceRumble.FL = p.SurfaceRumble.RL = 0.5f;
        }

        // --- tire grip ---
        float baseSlip = 0.15f + 0.05f * Noise(t, 7, 1f);
        p.TireCombinedSlip = new Wheels(baseSlip, baseSlip, baseSlip, baseSlip);
        p.TireSlipRatio = new Wheels(0.02f, 0.02f, 0.03f, 0.03f);
        if (t < 1.3)
        {
            float k = 1f - (float)(t / 1.3);
            p.TireSlipRatio.RL = p.TireSlipRatio.RR = 2.2f * k;
            p.TireCombinedSlip.RL = p.TireCombinedSlip.RR = 2.4f * k + 0.2f;
        }
        if (drift)
        {
            float rear = 2.0f + 0.5f * Noise(t, 8, 1.2f);
            p.TireCombinedSlip.RL = p.TireCombinedSlip.RR = rear;
            p.TireCombinedSlip.FL = p.TireCombinedSlip.FR = 0.7f;
            p.TireSlipRatio.RL = p.TireSlipRatio.RR = 0.6f;
            p.TireSlipAngle = new Wheels(0.08f, 0.08f, 0.45f, 0.45f);
        }
        if (lockup)
        {
            p.TireSlipRatio.FL = p.TireSlipRatio.FR = -1.3f;
            p.TireSlipRatio.RL = p.TireSlipRatio.RR = -0.4f;
            p.TireCombinedSlip.FL = p.TireCombinedSlip.FR = 1.4f;
        }
        p.WheelRotationSpeed = new Wheels(
            v / 0.34f * (1f + p.TireSlipRatio.FL), v / 0.34f * (1f + p.TireSlipRatio.FR),
            v / 0.34f * (1f + p.TireSlipRatio.RL), v / 0.34f * (1f + p.TireSlipRatio.RR));

        // --- puddle ---
        if (t >= 28.3 && t < 29.5) { p.WheelInPuddle.FR = 1f; p.WheelInPuddle.RR = 1f; }
        if (t >= 28.8 && t < 29.2) p.WheelInPuddle.FL = 1f;

        // --- fence ---
        if (t >= 29.9 && t < 29.95)
        {
            p.SmashableVelDiff = 6f;
            p.SmashableMass = 50f;
        }

        return p;
    }

    private static float SpeedAt(double t)
    {
        for (int i = 1; i < SpeedKeys.Length; i++)
        {
            if (t <= SpeedKeys[i].T)
            {
                var (t0, v0) = SpeedKeys[i - 1];
                var (t1, v1) = SpeedKeys[i];
                float k = (float)((t - t0) / (t1 - t0));
                return MathX.Lerp(v0, v1, MathX.Clamp01(k));
            }
        }
        return SpeedKeys[^1].V;
    }

    // Smooth deterministic noise in [-1, 1]
    private static float Noise(double t, int channel, float speed)
    {
        double x = t * speed;
        return (float)(0.5 * Math.Sin(x * 7.3 + channel * 1.7)
                     + 0.3 * Math.Sin(x * 13.1 + channel * 4.1)
                     + 0.2 * Math.Sin(x * 29.7 + channel * 2.3));
    }
}
