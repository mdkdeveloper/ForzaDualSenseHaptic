using HIDMaestro;

namespace ForzaHaptics.Emulation;

public enum XboxBackend { ViGEm, HidMaestro }

public sealed record XboxFeedback
{
    public byte Large { get; init; }
    public byte Small { get; init; }
    public bool LargePresent { get; init; } = true;
    public bool SmallPresent { get; init; } = true;
    public float? LeftTrigger { get; init; }
    public float? RightTrigger { get; init; }
    public string Source { get; init; } = "Unknown";
    public byte ReportId { get; init; }
    public uint Sequence { get; init; }
    public long Timestamp { get; init; } = Environment.TickCount64;
    public DateTimeOffset ReceivedUtc { get; init; } = DateTimeOffset.UtcNow;
    public byte[] Raw { get; init; } = [];
    public bool IsValid { get; init; } = true;
    public string? RejectionReason { get; init; }
    public int DurationMs { get; init; }
    public int DelayMs { get; init; }
    public int RepeatCount { get; init; }
    public bool IsTimed { get; init; }

    public static XboxFeedback FromViGEm(byte large, byte small) => new()
    {
        Large = large, Small = small, Source = "ViGEm", Raw = [large, small]
    };
}

/// <summary>Decodes only the pinned xbox-series-xs-bt descriptor and XUSB output.
/// The profile has no report IDs. HIDMaestro 1.9.0 nevertheless removes the
/// first byte of every HID write into ReportId, so it contains the motor mask
/// and Data contains LT, RT, large, small, duration, delay, extra plays.
/// An explicit zero report-ID prefix instead leaves all eight bytes in Data.
/// Magnitudes have logical maximum 100; descriptor time exponent is -2 seconds.</summary>
public static class XboxFeedbackDecoder
{
    public static XboxFeedback Decode(HMOutputSource source, byte reportId,
        ReadOnlySpan<byte> data, uint sequence, long timestamp, DateTimeOffset receivedUtc)
    {
        var result = new XboxFeedback
        {
            Source = source.ToString(), ReportId = reportId, Sequence = sequence,
            Timestamp = timestamp, ReceivedUtc = receivedUtc, Raw = data.ToArray(),
            LargePresent = false, SmallPresent = false
        };
        if (source == HMOutputSource.XInput)
        {
            // Driver forwards IOCTL_XUSB_SET_STATE unchanged. Extended packets
            // do not become HID/trigger packets merely because they are longer.
            if (reportId != 0 || data.Length is not (5 or 9) || data[0] != 0 || (data[4] & ~3) != 0)
                return Reject(result, "Malformed XInput feedback");
            if ((data[4] & 2) == 0)
                return Reject(result, "XInput LED/enumeration command has no vibration channel");
            return result with { Large = data[2], Small = data[3], LargePresent = true, SmallPresent = true };
        }
        if (source != HMOutputSource.HidOutput)
            return Reject(result, "Unsupported source/report ID for xbox-series-xs-bt");

        // driver.c forwards p[0] as ReportId and p[1..] as Data even for
        // descriptors without IDs. Only these two exact framings are valid;
        // never apply this reconstruction to XInput or another HID profile.
        byte mask;
        ReadOnlySpan<byte> channels;
        if (data.Length == 7)
        {
            mask = reportId;
            channels = data;
        }
        else if (reportId == 0 && data.Length == 8)
        {
            mask = data[0];
            channels = data[1..];
        }
        else
            return Reject(result, "Unsupported Xbox HID output framing");
        if ((mask & 0xF0) != 0)
            return Reject(result, "Malformed Xbox HID output length or reserved mask bits");
        if (channels[0] > 100 || channels[1] > 100 || channels[2] > 100 || channels[3] > 100)
            return Reject(result, "Xbox magnitude exceeds descriptor maximum 100");
        return result with
        {
            LeftTrigger = (mask & 8) != 0 ? channels[0] / 100f : null,
            RightTrigger = (mask & 4) != 0 ? channels[1] / 100f : null,
            Large = (byte)((channels[2] * 255 + 50) / 100),
            Small = (byte)((channels[3] * 255 + 50) / 100),
            LargePresent = (mask & 2) != 0, SmallPresent = (mask & 1) != 0,
            DurationMs = channels[4] * 10, DelayMs = channels[5] * 10,
            RepeatCount = channels[6], IsTimed = true
        };
    }

    private static XboxFeedback Reject(XboxFeedback result, string reason)
        => result with { IsValid = false, RejectionReason = reason };
}
