using System.Text.Json;
using ForzaHaptics.Emulation;

namespace ForzaHaptics.Tests;

public sealed class ImpulseDiagnosticsTests
{
    [Theory]
    [InlineData(0, "no-feedback")]
    [InlineData(1, "body-feedback-only")]
    [InlineData(2, "trigger-channels-present-but-zero")]
    [InlineData(3, "nonzero-impulse-triggers")]
    [InlineData(4, "no-valid-feedback")]
    public void FinalSummaryDistinguishesEvidenceWithoutInferringMissingChannels(int scenario, string expected)
    {
        using var folder = new TempFolder();
        using var logger = new ImpulseDiagnostics(folder.Path);
        logger.Start(new { backend = "test" });
        if (scenario > 0)
        {
            var feedback = Packet(1) with
            {
                LeftTrigger = scenario >= 2 ? 0 : null,
                RightTrigger = scenario == 3 ? 0.75f : scenario == 2 ? 0 : null,
                IsValid = scenario != 4
            };
            logger.RecordFeedback(feedback, "received");
        }
        logger.Stop();
        Assert.Null(logger.Error);
        var summary = Read(logger.SessionDirectory!).Single(x => Kind(x) == "session-summary").GetProperty("data");
        Assert.Equal(expected, summary.GetProperty("conclusion").GetString());
        Assert.Equal(scenario == 3 ? 1 : 0, summary.GetProperty("nonzeroRight").GetInt64());
        Assert.Equal(0, summary.GetProperty("nonzeroLeft").GetInt64());
        Assert.Equal(scenario == 3 ? 0.75f : 0f, summary.GetProperty("maximumRight").GetSingle());
    }

    [Fact]
    public void CapturesRejectedPacketsRawBytesTimingAndSequenceGapsBeforeFiltering()
    {
        using var folder = new TempFolder();
        using var logger = new ImpulseDiagnostics(folder.Path);
        logger.Start(new { backend = "test" });
        logger.RecordFeedback(Packet(uint.MaxValue), "accepted");
        logger.RecordFeedback(Packet(0), "accepted"); // Natural counter rollover is not a gap.
        logger.RecordFeedback(Packet(3) with
        {
            IsValid = false, RejectionReason = "malformed", Raw = [3, 255, 0],
            LeftTrigger = 1f, RightTrigger = null, DurationMs = 100, DelayMs = 20, RepeatCount = 2, IsTimed = true
        }, "rejected-decoder");
        logger.RecordFeedback(Packet(1), "priority-active"); // Backward reset is not billions of missing packets.
        logger.Stop();

        var rows = Read(logger.SessionDirectory!);
        var rejected = rows.Single(x => Kind(x) == "feedback" &&
            x.GetProperty("data").GetProperty("sequence").GetUInt32() == 3).GetProperty("data");
        Assert.Equal("03FF00", rejected.GetProperty("rawHex").GetString());
        Assert.True(rejected.GetProperty("leftTriggerPresent").GetBoolean());
        Assert.False(rejected.GetProperty("rightTriggerPresent").GetBoolean());
        Assert.Equal("malformed", rejected.GetProperty("rejectionReason").GetString());
        Assert.Equal("rejected-decoder", rejected.GetProperty("disposition").GetString());
        Assert.Equal(100, rejected.GetProperty("durationMs").GetInt32());
        Assert.Equal(20, rejected.GetProperty("delayMs").GetInt32());
        Assert.Equal(2, rejected.GetProperty("repeatCount").GetInt32());
        Assert.True(rejected.GetProperty("isTimed").GetBoolean());
        var summary = rows.Single(x => Kind(x) == "session-summary").GetProperty("data");
        Assert.Equal(2, summary.GetProperty("missingSequences").GetInt64());
        Assert.Equal(1, summary.GetProperty("sequenceResets").GetInt64());
        Assert.Equal(1, summary.GetProperty("invalidPackets").GetInt64());
        Assert.Equal(0, summary.GetProperty("nonzeroLeft").GetInt64());
    }

    [Fact]
    public void InterleavedSourcesShareSequenceAndReconnectionStartsFresh()
    {
        using var folder = new TempFolder();
        using var logger = new ImpulseDiagnostics(folder.Path);
        logger.Start(new { backend = "test" });
        logger.RecordFeedback(Packet(1) with { Source = "HidOutput" }, "accepted");
        logger.RecordFeedback(Packet(2) with { Source = "XInput" }, "accepted");
        logger.RecordFeedback(XboxFeedback.FromViGEm(1, 2), "accepted");
        logger.RecordFeedback(Packet(3) with { Source = "HidOutput" }, "accepted");
        logger.ResetSequence();
        logger.RecordFeedback(Packet(20) with { Source = "XInput" }, "accepted");
        logger.RecordFeedback(Packet(21) with { Source = "HidOutput" }, "accepted");
        logger.Stop();
        var summary = Read(logger.SessionDirectory!).Single(x => Kind(x) == "session-summary").GetProperty("data");
        Assert.Equal(0, summary.GetProperty("missingSequences").GetInt64());
        Assert.Equal(0, summary.GetProperty("sequenceResets").GetInt64());
    }

    [Fact]
    public void OverflowNeverBlocksProducersAndFinalTotalsIncludeDroppedPackets()
    {
        using var folder = new TempFolder();
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int opens = 0;
        using var logger = new ImpulseDiagnostics(folder.Path, capacity: 2, openStream: path =>
        {
            if (Interlocked.Increment(ref opens) == 1)
            {
                opened.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) throw new TimeoutException("Test did not release writer.");
            }
            return File.OpenWrite(path);
        });
        logger.Start(new { backend = "test" });
        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            for (uint i = 1; i <= 50; i++) logger.RecordFeedback(Packet(i) with { LeftTrigger = 0.5f }, "accepted");
        }
        finally { release.Set(); }
        logger.Stop();
        var rows = Read(logger.SessionDirectory!);
        var summary = rows.Single(x => Kind(x) == "session-summary").GetProperty("data");
        Assert.Equal(50, summary.GetProperty("packets").GetInt64());
        Assert.Equal(50, summary.GetProperty("nonzeroLeft").GetInt64());
        Assert.Equal(48, summary.GetProperty("droppedRecords").GetInt64());
        Assert.Equal(2, rows.Count(x => Kind(x) == "feedback"));
        Assert.Equal("nonzero-impulse-triggers", summary.GetProperty("conclusion").GetString());
    }

    [Fact]
    public void RotationKeepsCompleteJsonRecordsAndReadableCompanionFiles()
    {
        using var folder = new TempFolder();
        using var logger = new ImpulseDiagnostics(folder.Path, rotationBytes: 800);
        logger.Start(new { backend = "test" });
        for (int i = 0; i < 30; i++) logger.Record("hid-write", new { index = i, success = true, raw = new string('A', 100) });
        logger.Stop();
        Assert.Null(logger.Error);
        Assert.True(Directory.GetFiles(logger.SessionDirectory!, "packets-*.jsonl").Length > 1);
        Assert.True(Directory.GetFiles(logger.SessionDirectory!, "session-*.log").Length > 1);
        var rows = Read(logger.SessionDirectory!);
        Assert.Equal(Enumerable.Range(0, 30), rows.Where(x => Kind(x) == "hid-write")
            .Select(x => x.GetProperty("data").GetProperty("index").GetInt32()));
        Assert.Equal("session-start", Kind(rows.First()));
        Assert.Equal("session-summary", Kind(rows.Last()));
    }

    [Fact]
    public void DiskFailureIsVisibleAndDoesNotEscapeToControllerCallbacks()
    {
        using var folder = new TempFolder();
        using var logger = new ImpulseDiagnostics(folder.Path, openStream: _ => throw new IOException("disk unavailable"));
        logger.Start(new { backend = "test" });
        Assert.True(SpinWait.SpinUntil(() => logger.Error is not null, TimeSpan.FromSeconds(5)));
        logger.RecordFeedback(Packet(1), "accepted");
        logger.Record("hid-write", new { success = true });
        logger.Stop();
        Assert.False(logger.IsEnabled);
        Assert.Contains("disk unavailable", logger.Error);
    }

    [Fact]
    public async Task ActiveSessionFlushesPeriodicSummaryAndRestartHasIndependentTotals()
    {
        using var folder = new TempFolder();
        using var logger = new ImpulseDiagnostics(folder.Path);
        logger.Start(new { backend = "first" });
        string first = logger.SessionDirectory!;
        logger.RecordFeedback(Packet(1) with { RightTrigger = 1 }, "accepted");
        bool found = false;
        for (int i = 0; i < 50 && !found; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            found = Directory.Exists(first) && Directory.GetFiles(first, "*.jsonl")
                .Any(file => ReadActiveText(file).Contains("interval-summary"));
        }
        logger.Stop();
        Assert.True(found);
        logger.Start(new { backend = "second" });
        logger.Stop();
        Assert.NotEqual(first, logger.SessionDirectory);
        var summary = Read(logger.SessionDirectory!).Single(x => Kind(x) == "session-summary").GetProperty("data");
        Assert.Equal(0, summary.GetProperty("packets").GetInt64());
        Assert.Equal("no-feedback", summary.GetProperty("conclusion").GetString());
    }

    private static XboxFeedback Packet(uint sequence) => XboxFeedback.FromViGEm(20, 30) with
    {
        Source = "test-hid", ReportId = 3, Sequence = sequence, Raw = [3, 0, 1]
    };

    private static string? Kind(JsonElement row) => row.GetProperty("kind").GetString();

    private static string ReadActiveText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<JsonElement> Read(string directory) => Directory.GetFiles(directory, "packets-*.jsonl")
        .OrderBy(x => x, StringComparer.Ordinal).SelectMany(File.ReadAllLines)
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToList();

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaHaptics-Diagnostics-" + Guid.NewGuid());
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
