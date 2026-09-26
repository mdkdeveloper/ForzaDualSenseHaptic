using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForzaHaptics;

/// <summary>Read-only schema migration. Only an explicit Save writes the upgraded representation.</summary>
internal static class ConfigMigration
{
    public static AppConfig Parse(string json)
    {
        var root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new InvalidDataException("Configuration must be a JSON object");
        int version = root["ConfigVersion"]?.GetValue<int>() ?? 1;
        if (version is < 1 or > AppConfig.CurrentVersion)
            throw new InvalidDataException($"Unsupported ConfigVersion: {version}");
        bool migrated = version < AppConfig.CurrentVersion;
        if (version == 1) UpgradeLegacy(root);
        if (version < 3) UpgradePhysical(root);
        if (migrated) RemoveResistance(root);
        var config = root.Deserialize<AppConfig>(ConfigManager.JsonOptions)
            ?? throw new InvalidDataException("Configuration must be a JSON object");
        config.Validate();
        if (migrated)
            config.MigrationNotice = "Profile migrated in memory to version 4. Resistance curves have been removed; only vibration events remain. " +
                (version < 3 ? "Legacy ABS wall settings are ignored; vibration amplitudes are normalized and enabled events are independent of old Resistance mode. " : "") +
                "Slip settings, frequencies, intensities and explicit event permissions are preserved. The file is unchanged until Save.";
        return config;
    }

    private static void UpgradeLegacy(JsonObject root)
    {
        root["ConfigVersion"] = AppConfig.CurrentVersion;
        var triggers = Section(root, "Triggers");
        var throttle = Section(triggers, "Throttle");
        var brake = Section(triggers, "Brake");
        var shift = Section(triggers, "GearShift");
        var surface = Section(triggers, "Surface");
        var collision = Section(triggers, "Collision");

        // New defaults must not silently replace the old defaults of an omitted legacy field.
        SetMissing(throttle, "GripLoss", 0.6f);
        SetMissing(throttle, "VibModeStart", 5);
        SetMissing(throttle, "MaxVibration", 60);
        SetMissing(throttle, "VibAttackMs", 200f);
        SetMissing(throttle, "BoostResistance", 0.25f);
        Normalize(throttle, "VibAmpMin", 45, 255);
        Normalize(throttle, "VibAmpMax", 110, 255);
        throttle["ResistanceSource"] = nameof(TriggerResistanceSource.Acceleration);
        SetMissing(brake, "GripLoss", 0.05f);
        SetMissing(brake, "MaxResistance", 7);
        SetMissing(brake, "HandbrakeStrength", 8);
        SetMissing(brake, "MinVibration", 20);
        SetMissing(brake, "MaxVibration", 40);
        SetMissing(brake, "VibAttackMs", 150f);
        Normalize(brake, "AbsAmpMax", 4, 8);
        brake.Remove("AbsWallZones");
        brake.Remove("AbsWallStrength");
        foreach (var pedal in new[] { throttle, brake })
        {
            pedal["PulseDurationMs"] = 0f;
            pedal["VibReleaseMs"] = 0f;
            pedal["RearmMs"] = 0f;
        }
        SetMissing(shift, "Throttle", true);
        SetMissing(shift, "Brake", true);
        SetMissing(surface, "Throttle", true);
        SetMissing(surface, "Brake", true);
        SetMissing(collision, "Enabled", true);
        Normalize(shift, "Amp", 100, 255);
        Normalize(surface, "Amp", 10, 255);
        Normalize(surface, "StripAmp", 150, 255);
        Normalize(collision, "Amp", 255, 255);
    }

    private static void UpgradePhysical(JsonObject root)
    {
        root["ConfigVersion"] = 3;
        var triggers = Section(root, "Triggers");
        var throttle = Section(triggers, "Throttle");
        var brake = Section(triggers, "Brake");
        foreach (var pedal in new[] { throttle, brake })
        {
            var modeNode = pedal["Mode"];
            TriggerMode mode = modeNode == null ? TriggerMode.Vibration
                : modeNode.Deserialize<TriggerMode>(ConfigManager.JsonOptions);
            if (!Enum.IsDefined(mode)) throw new InvalidDataException("Unknown legacy trigger mode");
            pedal["Enabled"] = mode != TriggerMode.Off;
            pedal["ResistanceEnabled"] = mode != TriggerMode.Off;
            pedal["SlipEnabled"] = mode == TriggerMode.Vibration;
            float duration = pedal["PulseDurationMs"]?.GetValue<float>() ?? 300f;
            pedal["SlipMode"] = duration == 0 ? "Continuous" : "Repeated";
            SetMissing(pedal, "PulseDurationMs", duration);
            SetMissing(pedal, "MinResistance", 0);
            SetMissing(pedal, "MaxResistance", ReferenceEquals(pedal, throttle) ? 3 : 5);
            foreach (string name in new[] { "Mode", "ResistanceSource", "TurnAccelScale", "FwdAccelScale", "AccelLimit", "VibModeStart", "ResistanceSmoothing", "BoostResistance", "HandbrakeStrength", "RearmMs" })
                pedal.Remove(name);
        }
        throttle["LateralSlipEnabled"] = false;
        SetMissing(throttle, "VibAmpMin", 0.20f);
        SetMissing(throttle, "VibAmpMax", 0.25f);
        SetMissing(brake, "AbsAmpMax", 0.25f);
        SetMissing(brake, "VibAmpMin", 0f);
        var shift = Section(triggers, "GearShift");
        bool throttleShift = shift["Throttle"]?.GetValue<bool>() ?? false;
        bool brakeShift = shift["Brake"]?.GetValue<bool>() ?? false;
        shift["ThrottleUpshift"] = throttleShift;
        shift["ThrottleDownshift"] = throttleShift;
        shift["BrakeUpshift"] = brakeShift;
        shift["BrakeDownshift"] = brakeShift;
        SetMissing(shift, "Freq", 20);
        SetMissing(shift, "Amp", 0.25f);
        SetMissing(shift, "DurationMs", 60f);
        // Legacy pulses had no envelope. Preserve their short durations without invalidating them.
        SetMissing(shift, "AttackMs", 0f);
        SetMissing(shift, "ReleaseMs", 0f);
        shift.Remove("Throttle");
        shift.Remove("Brake");
    }
    private static void RemoveResistance(JsonObject root)
    {
        root["ConfigVersion"] = AppConfig.CurrentVersion;
        var triggers = Section(root, "Triggers");
        foreach (string channel in new[] { "Throttle", "Brake" })
        {
            var pedal = Section(triggers, channel);
            foreach (string name in new[] { "ResistanceEnabled", "StartZone", "MinResistance", "MaxResistance", "ResistanceSource", "ResistanceSmoothing", "BoostResistance", "HandbrakeStrength" })
                pedal.Remove(name);
        }
    }

    private static JsonObject Section(JsonObject parent, string name)
    {
        if (!parent.ContainsKey(name)) parent[name] = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        return parent[name] as JsonObject ?? throw new InvalidDataException(name + " must be an object");
    }

    private static void SetMissing<T>(JsonObject parent, string key, T value)
    {
        if (!parent.ContainsKey(key)) parent[key] = JsonValue.Create(value);
    }

    private static void Normalize(JsonObject parent, string key, float oldDefault, float divisor)
    {
        float legacy = parent.ContainsKey(key)
            ? parent[key]?.GetValue<float>() ?? throw new InvalidDataException(key + " must not be null") : oldDefault;
        if (!float.IsFinite(legacy) || legacy < 0 || legacy > divisor)
            throw new InvalidDataException($"Legacy {key} must be between 0 and {divisor}");
        parent[key] = legacy / divisor;
    }
}
