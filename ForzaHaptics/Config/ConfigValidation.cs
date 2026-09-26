using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;

namespace ForzaHaptics;

/// <summary>Reject invalid profiles before publication. Slider metadata also defines numeric ranges.</summary>
internal static class ConfigValidation
{
    public static void Validate(object config)
    {
        ValidateProperties(config, config.GetType().Name);
        switch (config)
        {
            case AppConfig c:
                Require(c.ConfigVersion == AppConfig.CurrentVersion, "Unsupported ConfigVersion");
                Require(c.Port is >= 1 and <= 65535, "Port must be between 1 and 65535");
                Require(c.Output is "auto" or "usb" or "bt", "Output must be auto, usb, or bt");
                Require(c.UsbHapticChannels.Length == 2, "UsbHapticChannels must contain 2 numbers, e.g. [2, 3]");
                Require(c.UsbHapticChannels.All(channel => channel >= 0), "USB channels must be nonnegative");
                Require(c.UsbHapticChannels.Distinct().Count() == 2, "USB haptic channels must be distinct");
                foreach (string target in c.ForwardTo)
                    Require(IPEndPoint.TryParse(target, out var endpoint) && endpoint.Port > 0,
                        $"Invalid forwarding endpoint: {target}");
                Ordered(c.LowCutHz, c.HighCutHz, "LowCutHz / HighCutHz", strict: true);
                break;
            case RoadConfig c: Ordered(c.MinFreqHz, c.MaxFreqHz, "Road frequency"); break;
            case RumbleStripConfig c: Ordered(c.MinFreqHz, c.MaxFreqHz, "RumbleStrip frequency"); break;
            case SuspensionConfig c: Ordered(c.ThresholdMps, c.FullMps, "Suspension threshold / full"); break;
            case SlipConfig c: Ordered(c.Start, c.Full, "Slip start / full"); break;
            case WheelspinConfig c: Ordered(c.Start, c.Full, "Wheelspin start / full"); break;
            case LockupConfig c: Ordered(c.Start, c.Full, "Lockup start / full"); break;
            case EngineConfig c: Ordered(c.MinFreqHz, c.MaxFreqHz, "Engine frequency"); break;
            case ImpactConfig c: Ordered(c.StartG, c.FullG, "Impact start / full"); break;
            case ThrottleTriggerConfig c:
                Ordered(c.VibFreqMin, c.MaxVibration, "Throttle vibration frequency");
                Ordered(c.VibAmpMin, c.VibAmpMax, "Throttle vibration amplitude");
                if (c.SlipMode == TriggerSlipMode.Repeated) {
                    Require(c.PulseDurationMs > 0, "Repeated slip requires a positive duration");
                    Pulse(c.PulseDurationMs, c.VibAttackMs, c.VibReleaseMs, "Throttle");
                }
                break;
            case BrakeTriggerConfig c:
                Ordered(c.MinVibration, c.MaxVibration, "Brake vibration frequency");
                Ordered(c.VibAmpMin, c.AbsAmpMax, "Brake vibration amplitude");
                if (c.SlipMode == TriggerSlipMode.Repeated) {
                    Require(c.PulseDurationMs > 0, "Repeated slip requires a positive duration");
                    Pulse(c.PulseDurationMs, c.VibAttackMs, c.VibReleaseMs, "Brake");
                }
                break;
            case TriggerGearShiftConfig c:
                Pulse(c.DurationMs, c.AttackMs, c.ReleaseMs, "Gear shift");
                break;
        }
    }

    private static void ValidateProperties(object config, string path)
    {
        foreach (var property in config.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
            var value = property.GetValue(config);
            string name = path + "." + property.Name;
            Require(value != null, name + " must not be null");
            if (value is float f) Require(float.IsFinite(f), name + " must be finite");
            if (value is double d) Require(double.IsFinite(d), name + " must be finite");
            if (property.PropertyType.IsEnum)
                Require(Enum.IsDefined(property.PropertyType, value!), name + " has an unknown enum value");
            if (value is int or float or double)
            {
                var ui = property.GetCustomAttribute<UiAttribute>();
                if (ui?.HasSlider == true)
                {
                    double number = Convert.ToDouble(value);
                    Require(number >= ui.Min && number <= ui.Max, $"{name} must be between {ui.Min} and {ui.Max}");
                }
            }
            if (property.PropertyType.GetCustomAttribute<UiGroupAttribute>() != null) Validate(value!);
        }
    }

    private static void Pulse(float duration, float attack, float release, string name)
    {
        if (duration > 0) Require(attack + release <= duration, name + " attack + release must not exceed pulse duration");
    }

    private static void Ordered(float min, float max, string name, bool strict = false) =>
        Require(strict ? min < max : min <= max, name + (strict ? " requires minimum < maximum" : " requires minimum <= maximum"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
