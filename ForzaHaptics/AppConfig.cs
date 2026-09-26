using System.Text.Json.Serialization;

namespace ForzaHaptics;

// Settings. The [Ui]/[UiGroup] attributes describe how to display a field in the window:
// label, slider range, and tooltip. Profiles are stored in the Configs folder next to the executable.

/// <summary>Describes a field in the settings window.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UiAttribute : Attribute
{
    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }

    /// <summary>A field with a slider (for numbers) in the min..max range.</summary>
    public UiAttribute(string label, double min, double max, double step)
    {
        Label = label;
        Min = min;
        Max = max;
        Step = step;
    }

    /// <summary>A field without a slider: checkbox, list, or text.</summary>
    public UiAttribute(string label)
    {
        Label = label;
    }

    public string? Tip { get; set; }
    /// <summary>Group name when the field does not belong to its class group (for top-level fields).</summary>
    public string? Group { get; set; }
    /// <summary>Comma-separated choices for a string field.</summary>
    public string? Choices { get; set; }
    /// <summary>The change takes effect only after restarting the output.</summary>
    public bool Restart { get; set; }
    public bool HasSlider => Max > Min;
}

/// <summary>A settings section: group heading and tab.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UiGroupAttribute : Attribute
{
    public string Title { get; }
    public string Tab { get; }
    public string? Tip { get; set; }

    public UiGroupAttribute(string title, string tab)
    {
        Title = title;
        Tab = tab;
    }
}

public static class UiTabs
{
    public const string General = "General";
    public const string Vibration = "Vibration";
    public const string Triggers = "Triggers";
}

[UiGroup("Strength and frequencies", UiTabs.General)]
public sealed class AppConfig
{
    public const int CurrentVersion = 4;
    public int ConfigVersion { get; set; } = CurrentVersion;
    [JsonIgnore] public string? MigrationNotice { get; internal set; }

    private const string Connection = "Connection";

    [Ui("UDP port", Group = Connection, Restart = true,
        Tip = "Enter the same port in FH6: Settings → HUD and Gameplay → Data Out IP Port")]
    public int Port { get; set; } = 5310;

    [Ui("Forward to", Group = Connection, Restart = true,
        Tip = "Forward packets to other applications, separated by commas, e.g. 127.0.0.1:5300 (only one application can listen on a UDP port)")]
    public List<string> ForwardTo { get; set; } = new();

    [Ui("Output", Group = Connection, Choices = "auto,usb,bt", Restart = true,
        Tip = "auto — try USB audio first, then Bluetooth")]
    public string Output { get; set; } = "auto";

    [Ui("USB latency, ms", 3, 200, 1, Group = Connection, Restart = true,
        Tip = "USB audio buffer. Lower values respond faster but may cause audio dropouts")]
    public int UsbLatencyMs { get; set; } = 20;

    [Ui("USB haptic channels", Group = Connection, Restart = true,
        Tip = "Zero-based audio channel indices for the left and right motors: 2, 3")]
    public int[] UsbHapticChannels { get; set; } = { 2, 3 };

    [Ui("Overall strength", 0, 4, 0.05, Tip = "Multiplier for all controller-body vibration")]
    public float MasterGain { get; set; } = 1.0f;

    [Ui("Swap left/right")]
    public bool SwapLeftRight { get; set; }

    [Ui("Low cutoff, Hz", 5, 100, 1,
        Tip = "Below ~60 Hz the DualSense actuators are barely perceptible, so those frequencies only consume headroom")]
    public float LowCutHz { get; set; } = 45f;

    [Ui("High cutoff, Hz", 80, 1000, 5, Tip = "Above ~350 Hz the motors begin to whine")]
    public float HighCutHz { get; set; } = 350f;

    public DynamicsConfig Dynamics { get; set; } = new();

    public RoadConfig Road { get; set; } = new();
    public RumbleStripConfig RumbleStrip { get; set; } = new();
    public SuspensionConfig Suspension { get; set; } = new();
    public SlipConfig Slip { get; set; } = new();
    public WheelspinConfig Wheelspin { get; set; } = new();
    public LockupConfig Lockup { get; set; } = new();
    public EngineConfig Engine { get; set; } = new();
    public GearShiftConfig GearShift { get; set; } = new();
    public ImpactConfig Impact { get; set; } = new();
    public WaterConfig Water { get; set; } = new();

    public TriggersConfig Triggers { get; set; } = new();

    public void Validate() => ConfigValidation.Validate(this);
}

[UiGroup("Compressor", UiTabs.General,
    Tip = "Raises quiet textures and keeps impacts from clipping abruptly. Compression is shared by both motors")]
public sealed class DynamicsConfig
{
    [Ui("Enabled")]
    public bool Enabled { get; set; } = true;
    [Ui("Threshold", 0.01, 1, 0.01, Tip = "Level above which the signal is compressed")]
    public float Threshold { get; set; } = 0.25f;
    [Ui("Compression ratio", 1, 20, 0.1, Tip = "3 = one-third the gain increase above the threshold")]
    public float Ratio { get; set; } = 3f;
    [Ui("Makeup gain", 0, 8, 0.1, Tip = "Overall strength after compression — the main stronger/weaker control")]
    public float Makeup { get; set; } = 3.0f;
    [Ui("Attack, ms", 0.1, 100, 0.1)]
    public float AttackMs { get; set; } = 3f;
    [Ui("Release, ms", 5, 2000, 5)]
    public float ReleaseMs { get; set; } = 150f;

    public void Validate() => ConfigValidation.Validate(this);
}

[UiGroup("Road texture", UiTabs.Vibration, Tip = "Noise based on the game's SurfaceRumble value and speed")]
public sealed class RoadConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.55f;
    [Ui("Asphalt roughness", 0, 1, 0.01, Tip = "Base texture added to SurfaceRumble")]
    public float AsphaltBase { get; set; } = 0.12f;
    [Ui("Surface multiplier", 0, 5, 0.05, Tip = "How much stronger dirt/cobblestone feels than asphalt")]
    public float SurfaceScale { get; set; } = 1.5f;
    [Ui("Minimum frequency, Hz", 20, 400, 1)] public float MinFreqHz { get; set; } = 70f;
    [Ui("Maximum frequency, Hz", 20, 400, 1)] public float MaxFreqHz { get; set; } = 190f;
    [Ui("Full strength at, km/h", 20, 400, 5)] public float FullSpeedKmh { get; set; } = 160f;
}

[UiGroup("Rumble strips", UiTabs.Vibration, Tip = "Rhythm = speed / strip spacing, at the carrier frequency")]
public sealed class RumbleStripConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.5f;
    [Ui("Strip spacing, m", 0.1, 2, 0.01)] public float SpacingMeters { get; set; } = 0.45f;
    [Ui("Minimum rhythm, Hz", 1, 200, 1)] public float MinFreqHz { get; set; } = 12f;
    [Ui("Maximum rhythm, Hz", 1, 300, 1)] public float MaxFreqHz { get; set; } = 110f;
    [Ui("Carrier, Hz", 40, 400, 1)] public float CarrierHz { get; set; } = 160f;
}

[UiGroup("Potholes, joints, and landings", UiTabs.Vibration, Tip = "Impacts based on suspension travel speed")]
public sealed class SuspensionConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.8f;
    [Ui("Threshold, m/s", 0, 3, 0.05, Tip = "Suspension travel speed at which an impact begins")]
    public float ThresholdMps { get; set; } = 0.3f;
    [Ui("Full strength, m/s", 0.1, 10, 0.1)] public float FullMps { get; set; } = 2.5f;
    [Ui("Frequency, Hz", 20, 400, 1)] public float FreqHz { get; set; } = 85f;
    [Ui("Decay, ms", 5, 500, 5)] public float DecayMs { get; set; } = 70f;
}

[UiGroup("Grip loss", UiTabs.Vibration, Tip = "Combined-slip cue; does not distinguish oversteer from understeer")]
public sealed class SlipConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.45f;
    [Ui("Start", 0, 5, 0.05)] public float Start { get; set; } = 0.9f;
    [Ui("Full strength", 0, 10, 0.1)] public float Full { get; set; } = 2.5f;
    [Ui("Frequency, Hz", 20, 400, 1)] public float FreqHz { get; set; } = 120f;
}

[UiGroup("Wheelspin", UiTabs.Vibration, Tip = "Under throttle: TireSlipRatio")]
public sealed class WheelspinConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.3f;
    [Ui("Start", 0, 5, 0.05)] public float Start { get; set; } = 1.0f;
    [Ui("Full strength", 0, 10, 0.1)] public float Full { get; set; } = 3.0f;
    [Ui("Frequency, Hz", 20, 400, 1)] public float FreqHz { get; set; } = 150f;
}

[UiGroup("Braking slip", UiTabs.Vibration, Tip = "Inferred longitudinal slip during braking; not measured ABS activation")]
public sealed class LockupConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.5f;
    [Ui("Start", 0, 5, 0.05)] public float Start { get; set; } = 0.9f;
    [Ui("Full strength", 0, 10, 0.1)] public float Full { get; set; } = 1.5f;
    [Ui("Pulse, Hz", 1, 40, 0.5)] public float PulseHz { get; set; } = 14f;
    [Ui("Carrier, Hz", 20, 400, 1)] public float CarrierHz { get; set; } = 150f;
}

[UiGroup("Engine", UiTabs.Vibration, Tip = "Frequency follows RPM; kept quiet so it does not overpower road feedback")]
public sealed class EngineConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 1, 0.01)] public float Gain { get; set; } = 0.1f;
    [Ui("Minimum frequency, Hz", 20, 400, 1)] public float MinFreqHz { get; set; } = 55f;
    [Ui("Maximum frequency, Hz", 20, 400, 1)] public float MaxFreqHz { get; set; } = 190f;
    [Ui("Limiter starts at", 0.5, 1, 0.01, Tip = "Fraction of maximum RPM")]
    public float LimiterThreshold { get; set; } = 0.97f;
    [Ui("Limiter strength", 0, 1, 0.01)] public float LimiterGain { get; set; } = 0.2f;
    [Ui("Limiter rhythm, Hz", 1, 40, 0.5)] public float LimiterHz { get; set; } = 15f;
}

[UiGroup("Gear shifts", UiTabs.Vibration)]
public sealed class GearShiftConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.55f;
    [Ui("Frequency, Hz", 20, 400, 1)] public float FreqHz { get; set; } = 110f;
    [Ui("Decay, ms", 5, 500, 5)] public float DecayMs { get; set; } = 45f;
}

[UiGroup("Collisions", UiTabs.Vibration, Tip = "Peak horizontal acceleration + SmashableVelDiff")]
public sealed class ImpactConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 1.0f;
    [Ui("Threshold, g", 0.5, 20, 0.1, Tip = "Normal driving stays below ~2 g")]
    public float StartG { get; set; } = 3.5f;
    [Ui("Full strength, g", 1, 40, 0.5)] public float FullG { get; set; } = 15f;
    [Ui("Frequency, Hz", 20, 400, 1)] public float FreqHz { get; set; } = 75f;
    [Ui("Crunch, Hz", 20, 400, 1, Tip = "Noise layer for the impact")]
    public float NoiseFreqHz { get; set; } = 170f;
    [Ui("Object impact, Hz", 20, 400, 1)] public float SmashFreqHz { get; set; } = 90f;
    [Ui("Decay, ms", 5, 500, 5)] public float DecayMs { get; set; } = 140f;
    [Ui("Object: full strength, m/s", 1, 40, 0.5)] public float SmashableFullVel { get; set; } = 10f;
}

[UiGroup("Puddles", UiTabs.Vibration)]
public sealed class WaterConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Strength", 0, 2, 0.01)] public float Gain { get; set; } = 0.35f;
    [Ui("Splash strength", 0, 2, 0.01)] public float SplashGain { get; set; } = 0.6f;
    [Ui("Frequency, Hz", 20, 400, 1)] public float FreqHz { get; set; } = 75f;
    [Ui("Splash, Hz", 20, 400, 1)] public float SplashFreqHz { get; set; } = 140f;
}

// ---------------- L2/R2 adaptive triggers ----------------
// Logic and default values are based on HorizonHaptics (github.com/haritha99ch/HorizonHaptics).

/// <summary>Pedal pressure for new profiles; signed longitudinal acceleration source for migrated profiles.</summary>
public enum TriggerSlipMode { Continuous, Repeated }

public enum TriggerResistanceSource { Pedal, Acceleration }

/// <summary>Legacy version 1/2 modes used only while migrating profiles.</summary>
public enum TriggerMode
{
    Off,
    Resistance,
    Vibration,
}

[UiGroup("Triggers", UiTabs.Triggers, Tip = "L2/R2 adaptive triggers")]
public sealed class TriggersConfig
{
    [Ui("Enabled", Restart = true)] public bool Enabled { get; set; } = true;
    [Ui("Overall strength", 0, 1, 0.01, Tip = "Scales trigger effects; this is a comfort control, not a certified wear limit")]
    public float Strength { get; set; } = 0.65f;
    public TriggerHysteresisConfig Hysteresis { get; set; } = new();
    public ThrottleTriggerConfig Throttle { get; set; } = new();
    public BrakeTriggerConfig Brake { get; set; } = new();
    public TriggerGearShiftConfig GearShift { get; set; } = new();
    public TriggerSurfaceConfig Surface { get; set; } = new();
    public TriggerCollisionConfig Collision { get; set; } = new();

    public void Validate() => ConfigValidation.Validate(this);
}

[UiGroup("Hysteresis", UiTabs.Triggers,
    Tip = "Entry and exit thresholds differ, so the previous state is held inside the range and the trigger does not chatter at the boundary")]
public sealed class TriggerHysteresisConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = true;
    [Ui("Slip band", 0, 0.9, 0.01, Tip = "Exit inferred slip warning at GripLoss × (1 − this value)")]
    public float SlipBand { get; set; } = 0.2f;
    public int PedalBand { get; set; } = 20;
    public float LevelDeadband { get; set; } = 0.35f;
    public int VibDeadband { get; set; } = 3;
    public float HoldMs { get; set; } = 150f;
}

[UiGroup("R2 — throttle", UiTabs.Triggers, Tip = "Independent wheelspin and lateral-slip vibration cues")]
public sealed class ThrottleTriggerConfig
{
    [Ui("Channel enabled (all effects)", Tip = "Master switch including gear shifts. To keep shifts only, leave this on and turn off the slip effects below.")] public bool Enabled { get; set; } = true;
    [JsonIgnore] public bool ResistanceEnabled { get; set; } = false;
    [Ui("Wheelspin", Tip = "Longitudinal slip of driven wheels under physically pressed R2; braking suppresses this cue")] public bool SlipEnabled { get; set; } = true;
    [Ui("Lateral slip")] public bool LateralSlipEnabled { get; set; } = true;
    [JsonIgnore] public int StartZone { get; set; } = 1;
    [Ui("Slip pattern")] public TriggerSlipMode SlipMode { get; set; } = TriggerSlipMode.Continuous;
    [Ui("Repeated pulse pause, ms", 0, 5000, 10)] public float PulsePauseMs { get; set; } = 250f;
    [JsonIgnore] public TriggerResistanceSource ResistanceSource { get; set; } = TriggerResistanceSource.Pedal;
    [JsonIgnore] public TriggerMode Mode { get; set; } = TriggerMode.Vibration;
    [Ui("Intensity", 0, 2, 0.05)] public float Intensity { get; set; } = 0.7f;
    [Ui("Slip threshold", 0, 5, 0.05, Tip = "Normalized longitudinal slip threshold for an inferred grip warning")]
    public float GripLoss { get; set; } = 0.9f;
    // Legacy source compatibility only; ignored by serialization and the event-only processor.
    [JsonIgnore] public float TurnAccelScale { get; set; } = 0.25f;
    [JsonIgnore] public float FwdAccelScale { get; set; } = 1.0f;
    [JsonIgnore] public float AccelLimit { get; set; } = 10f;
    [JsonIgnore] public int VibModeStart { get; set; } = 20;
    [Ui("Minimum vibration frequency, Hz", 0, 255, 1, Tip = "Below ~25, pulses feel like individual taps on the finger")]
    public int VibFreqMin { get; set; } = 30;
    [Ui("Maximum vibration frequency, Hz", 0, 255, 1)] public int MaxVibration { get; set; } = 45;
    [Ui("Frequency smoothing", 0.01, 1, 0.01, Tip = "1 = no smoothing; lower values are smoother")]
    public float VibSmoothing { get; set; } = 1.0f;
    [Ui("Minimum vibration strength", 0, 1, 0.01, Tip = "At the slip threshold")]
    public float VibAmpMin { get; set; } = 0.35f;
    [Ui("Maximum vibration strength", 0, 1, 0.01, Tip = "During heavy slip; normalized amplitude, not a force measurement")]
    public float VibAmpMax { get; set; } = 0.5f;
    [Ui("Attack, ms", 0, 2000, 10, Tip = "Time for vibration to ramp smoothly from zero")]
    public float VibAttackMs { get; set; } = 80f;
    [Ui("Pulse duration, ms", 0, 2000, 10, Tip = "Duration used by the repeated slip pattern")]
    public float PulseDurationMs { get; set; } = 250f;
    [Ui("Release, ms", 0, 2000, 10)] public float VibReleaseMs { get; set; } = 80f;
    [JsonIgnore] public float RearmMs { get; set; } = 500f;
    [JsonIgnore] public int MinResistance { get; set; } = 2;
    [JsonIgnore] public int MaxResistance { get; set; } = 5;
    [JsonIgnore] public float ResistanceSmoothing { get; set; } = 0.9f;
    [JsonIgnore] public float BoostResistance { get; set; } = 0f;
}

[UiGroup("L2 — brake", UiTabs.Triggers, Tip = "Inferred braking-slip vibration; not a measured ABS state")]
public sealed class BrakeTriggerConfig
{
    [Ui("Channel enabled (all effects)", Tip = "Master switch including gear shifts. To keep shifts only, leave this on and turn off the slip effects below.")] public bool Enabled { get; set; } = true;
    [JsonIgnore] public bool ResistanceEnabled { get; set; } = false;
    [Ui("Braking slip")] public bool SlipEnabled { get; set; } = true;
    [JsonIgnore] public int StartZone { get; set; } = 1;
    [Ui("Slip pattern")] public TriggerSlipMode SlipMode { get; set; } = TriggerSlipMode.Continuous;
    [Ui("Repeated pulse pause, ms", 0, 5000, 10)] public float PulsePauseMs { get; set; } = 250f;
    [Ui("Minimum vibration strength", 0, 1, 0.01)] public float VibAmpMin { get; set; } = 0.35f;
    [JsonIgnore] public TriggerMode Mode { get; set; } = TriggerMode.Vibration;
    [Ui("Intensity", 0, 2, 0.05)] public float Intensity { get; set; } = 0.7f;
    [Ui("Slip threshold", 0, 5, 0.01)] public float GripLoss { get; set; } = 0.9f;
    [JsonIgnore] public int MinResistance { get; set; } = 3;
    [JsonIgnore] public int MaxResistance { get; set; } = 7;
    [JsonIgnore] public float ResistanceSmoothing { get; set; } = 0.4f;
    [JsonIgnore] public int HandbrakeStrength { get; set; } = 0;
    [Ui("Slip pulse strength", 0, 1, 0.01)] public float AbsAmpMax { get; set; } = 0.5f;
    [Ui("Braking slip: minimum frequency, Hz", 0, 255, 1)] public int MinVibration { get; set; } = 25;
    [Ui("Braking slip: maximum frequency, Hz", 0, 255, 1)] public int MaxVibration { get; set; } = 35;
    [Ui("Frequency smoothing", 0.01, 1, 0.01)] public float VibSmoothing { get; set; } = 0.8f;
    [Ui("Braking slip attack, ms", 0, 2000, 10)] public float VibAttackMs { get; set; } = 80f;
    [Ui("Pulse duration, ms", 0, 2000, 10, Tip = "Duration used by the repeated slip pattern")]
    public float PulseDurationMs { get; set; } = 250f;
    [Ui("Release, ms", 0, 2000, 10)] public float VibReleaseMs { get; set; } = 80f;
    [JsonIgnore] public float RearmMs { get; set; } = 500f;
}

[UiGroup("Gear-shift pulse", UiTabs.Triggers, Tip = "Optional cue requiring an engaged pedal")]
public sealed class TriggerGearShiftConfig
{
    [JsonIgnore] public bool Throttle { get; set; } = false;
    [JsonIgnore] public bool Brake { get; set; } = false;
    [Ui("R2 upshift")] public bool ThrottleUpshift { get; set; } = true;
    [Ui("R2 downshift")] public bool ThrottleDownshift { get; set; } = true;
    [Ui("L2 upshift")] public bool BrakeUpshift { get; set; } = false;
    [Ui("L2 downshift")] public bool BrakeDownshift { get; set; } = true;
    [Ui("Frequency, Hz", 0, 255, 1)] public int Freq { get; set; } = 20;
    [Ui("Strength", 0, 1, 0.01)] public float Amp { get; set; } = 1.0f;
    [Ui("Duration, ms", 0, 500, 5)] public float DurationMs { get; set; } = 300f;
    [Ui("Attack, ms", 0, 500, 5)] public float AttackMs { get; set; } = 50f;
    [Ui("Release, ms", 0, 500, 5)] public float ReleaseMs { get; set; } = 50f;
}
[UiGroup("Optional road cues", UiTabs.Triggers, Tip = "Road texture and rumble-strip cues require an engaged pedal")]
public sealed class TriggerSurfaceConfig
{
    [Ui("On R2")] public bool Throttle { get; set; } = false;
    [Ui("On L2")] public bool Brake { get; set; } = false;
    [Ui("Road: frequency, Hz", 0, 255, 1)] public int Freq { get; set; } = 10;
    [Ui("Road: strength", 0, 1, 0.01)] public float Amp { get; set; } = 0.125f;
    [Ui("Rumble strip: frequency, Hz", 0, 255, 1)] public int StripFreq { get; set; } = 25;
    [Ui("Rumble strip: strength", 0, 1, 0.01)] public float StripAmp { get; set; } = 0.25f;
}

[UiGroup("Collision jolt", UiTabs.Triggers, Tip = "Optional object-impact cue; each trigger requires an engaged pedal")]
public sealed class TriggerCollisionConfig
{
    [Ui("Enabled")] public bool Enabled { get; set; } = false;
    [Ui("Threshold, m/s", 0, 30, 0.5)] public float ThresholdMps { get; set; } = 3f;
    [Ui("Frequency, Hz", 0, 255, 1)] public int Freq { get; set; } = 40;
    [Ui("Strength", 0, 1, 0.01)] public float Amp { get; set; } = 0.25f;
    [Ui("Duration, ms", 0, 500, 5)] public float DurationMs { get; set; } = 150f;
}
