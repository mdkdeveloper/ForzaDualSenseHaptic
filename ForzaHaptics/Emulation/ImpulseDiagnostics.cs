using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ForzaHaptics.Util;

namespace ForzaHaptics.Emulation;

/// <summary>Session diagnostics. Controller callbacks only enqueue records; one worker owns all file I/O.</summary>
public sealed class ImpulseDiagnostics : IDisposable
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaHaptics", "Logs");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly object _lifecycle = new();
    private readonly string _directory;
    private readonly int _capacity;
    private readonly long _rotationBytes;
    private readonly Func<string, Stream> _openStream;
    private Session? _session;
    private string? _error;
    private string? _sessionDirectory;
    private bool _disposed;

    public ImpulseDiagnostics() : this(LogDirectory) { }

    internal ImpulseDiagnostics(string directory, int capacity = 4096, long rotationBytes = 32 * 1024 * 1024,
        Func<string, Stream>? openStream = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rotationBytes);
        _directory = directory;
        _capacity = capacity;
        _rotationBytes = rotationBytes;
        _openStream = openStream ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 65536, FileOptions.SequentialScan));
    }

    public bool IsEnabled => Volatile.Read(ref _session) is { Failed: false };
    public string? Error => Volatile.Read(ref _error);
    public string? SessionDirectory => Volatile.Read(ref _sessionDirectory);

    public void Start(object metadata)
    {
        lock (_lifecycle)
        {
            if (_disposed || IsEnabled) return;
            StopCore();
            Volatile.Write(ref _error, null);
            var path = Path.Combine(_directory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
            Volatile.Write(ref _sessionDirectory, path);
            var session = new Session(_capacity);
            Volatile.Write(ref _session, session);
            session.Queue.Writer.TryWrite(new Entry(DateTimeOffset.UtcNow, "session-start", metadata));
            session.Worker = Task.Run(() => WriteSessionAsync(session, path));
        }
    }

    public void Record(string kind, object data)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Failed) return;
        lock (session.Gate)
        {
            if (session.Closed || session.Failed) return;
            Enqueue(session, new Entry(DateTimeOffset.UtcNow, kind, data));
        }
    }

    /// <summary>Call for every received packet, including packets rejected by output ownership or decoding.</summary>
    public void RecordFeedback(XboxFeedback feedback, string disposition)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Failed) return;
        lock (session.Gate)
        {
            if (session.Closed || session.Failed) return;
            long missing = 0;
            bool sequenceReset = false;
            // HIDMaestro's sequence belongs to the controller's shared output ring,
            // not to an individual source. ViGEm does not supply sequence numbers.
            if (feedback.Source != "ViGEm" && session.LastSequence is { } previous)
            {
                uint delta = unchecked(feedback.Sequence - previous);
                if (delta > 1 && delta < 0x80000000) missing = delta - 1;
                else if (delta >= 0x80000000) sequenceReset = true;
            }
            if (feedback.Source != "ViGEm") session.LastSequence = feedback.Sequence;
            session.Total.Add(feedback, missing, sequenceReset);
            session.Interval.Add(feedback, missing, sequenceReset);
            Enqueue(session, new Entry(DateTimeOffset.UtcNow, "feedback", new
            {
                feedback.Source, feedback.ReportId, feedback.Sequence, feedback.Timestamp, feedback.ReceivedUtc,
                rawHex = Convert.ToHexString(feedback.Raw),
                feedback.Large, feedback.Small, feedback.LeftTrigger, feedback.RightTrigger,
                feedback.LargePresent, feedback.SmallPresent,
                leftTriggerPresent = feedback.LeftTrigger.HasValue, rightTriggerPresent = feedback.RightTrigger.HasValue,
                feedback.IsValid, feedback.RejectionReason, disposition,
                feedback.DurationMs, feedback.DelayMs, feedback.RepeatCount, feedback.IsTimed,
                missingSequenceNumbers = missing, sequenceReset
            }));
        }
    }

    public void Stop()
    {
        lock (_lifecycle) StopCore();
    }

    /// <summary>Call when a new virtual controller is created; its output ring starts a new sequence.</summary>
    public void ResetSequence()
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Failed) return;
        lock (session.Gate)
        {
            if (session.Closed || session.Failed) return;
            session.LastSequence = null;
            Enqueue(session, new Entry(DateTimeOffset.UtcNow, "sequence-reset", new { reason = "new-virtual-controller" }));
        }
    }

    private void StopCore()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        lock (session.Gate)
        {
            session.Closed = true;
            session.Queue.Writer.TryComplete();
        }
        // Completion drains accepted entries and writes the cumulative summary before returning.
        session.Worker?.GetAwaiter().GetResult();
    }

    private static void Enqueue(Session session, Entry entry)
    {
        if (!session.Queue.Writer.TryWrite(entry))
        {
            session.Total.DroppedRecords++;
            session.Interval.DroppedRecords++;
        }
    }

    private async Task WriteSessionAsync(Session session, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var json = new RotatingFile(directory, "packets", "jsonl", _rotationBytes, _openStream);
            using var readable = new RotatingFile(directory, "session", "log", _rotationBytes, _openStream);
            void Write(Entry entry)
            {
                string payload = JsonSerializer.Serialize(entry.Data, JsonOptions);
                json.Write(JsonSerializer.Serialize(new { utc = entry.Utc, kind = entry.Kind, data = entry.Data }, JsonOptions));
                readable.Write($"{entry.Utc:O} [{entry.Kind}] {payload}");
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            Task<bool> tick = timer.WaitForNextTickAsync().AsTask();
            Task<bool> available = session.Queue.Reader.WaitToReadAsync().AsTask();
            while (true)
            {
                await Task.WhenAny(available, tick).ConfigureAwait(false);
                // Bound each drain so sustained traffic cannot starve statistics or flushing.
                for (int i = 0; i < _capacity && session.Queue.Reader.TryRead(out var entry); i++) Write(entry);
                if (tick.IsCompleted)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    object interval;
                    lock (session.Gate)
                    {
                        interval = session.Interval.Snapshot();
                        session.Interval = new Counters();
                    }
                    Write(new Entry(DateTimeOffset.UtcNow, "interval-summary", interval));
                    json.Flush();
                    readable.Flush();
                    tick = timer.WaitForNextTickAsync().AsTask();
                }
                if (available.IsCompleted)
                {
                    if (!await available.ConfigureAwait(false)) break;
                    available = session.Queue.Reader.WaitToReadAsync().AsTask();
                }
            }
            object summary;
            lock (session.Gate) summary = session.Total.Snapshot();
            Write(new Entry(DateTimeOffset.UtcNow, "session-summary", summary));
            json.Flush();
            readable.Flush();
        }
        catch (Exception ex)
        {
            session.Failed = true;
            session.Queue.Writer.TryComplete();
            while (session.Queue.Reader.TryRead(out _)) { }
            string message = $"Impulse diagnostics stopped: {ex.Message}";
            Volatile.Write(ref _error, message);
            try { Log.Warn(message); } catch { /* A logging failure must never escape into controller handling. */ }
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            StopCore();
        }
    }

    private sealed record Entry(DateTimeOffset Utc, string Kind, object Data);

    private sealed class Session(int capacity)
    {
        public readonly object Gate = new();
        public readonly Channel<Entry> Queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        public uint? LastSequence;
        public Counters Total = new(), Interval = new();
        public Task? Worker;
        public volatile bool Failed;
        public bool Closed;
    }

    private sealed class Counters
    {
        public readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
        public readonly Dictionary<string, long> PacketTypes = new(StringComparer.Ordinal);
        public long Packets, InvalidPackets, TriggerPackets, NonzeroLeft, NonzeroRight, MissingSequences, SequenceResets, DroppedRecords;
        public float MaximumLeft, MaximumRight;

        public void Add(XboxFeedback feedback, long missing, bool reset)
        {
            Packets++;
            string type = $"{feedback.Source}/0x{feedback.ReportId:X2}";
            PacketTypes[type] = PacketTypes.GetValueOrDefault(type) + 1;
            MissingSequences += missing;
            if (reset) SequenceResets++;
            if (!feedback.IsValid) { InvalidPackets++; return; }
            if (feedback.LeftTrigger.HasValue || feedback.RightTrigger.HasValue) TriggerPackets++;
            if (feedback.LeftTrigger is > 0) NonzeroLeft++;
            if (feedback.RightTrigger is > 0) NonzeroRight++;
            if (feedback.LeftTrigger is { } left && float.IsFinite(left)) MaximumLeft = Math.Max(MaximumLeft, left);
            if (feedback.RightTrigger is { } right && float.IsFinite(right)) MaximumRight = Math.Max(MaximumRight, right);
        }

        public object Snapshot() => new
        {
            StartedUtc, endedUtc = DateTimeOffset.UtcNow, Packets, InvalidPackets, TriggerPackets,
            NonzeroLeft, NonzeroRight, MaximumLeft, MaximumRight, MissingSequences, SequenceResets, DroppedRecords,
            packetTypes = new Dictionary<string, long>(PacketTypes),
            conclusion = Packets == 0 ? "no-feedback" : Packets == InvalidPackets ? "no-valid-feedback" :
                NonzeroLeft > 0 || NonzeroRight > 0 ? "nonzero-impulse-triggers" :
                TriggerPackets > 0 ? "trigger-channels-present-but-zero" : "body-feedback-only",
            evidence = "Received commands only; successful HID writes do not confirm a physical controller effect."
        };
    }

    private sealed class RotatingFile(string directory, string prefix, string extension, long limit,
        Func<string, Stream> openStream) : IDisposable
    {
        private Stream? _stream;
        private long _bytes;
        private int _part;

        public void Write(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            if (_stream is null || (_bytes != 0 && _bytes + bytes.Length > limit))
            {
                _stream?.Dispose();
                _stream = null;
                _stream = openStream(Path.Combine(directory, $"{prefix}-{++_part:D4}.{extension}"));
                _bytes = 0;
            }
            _stream.Write(bytes);
            _bytes += bytes.Length;
        }

        public void Flush() => _stream?.Flush();
        public void Dispose() => _stream?.Dispose();
    }
}
