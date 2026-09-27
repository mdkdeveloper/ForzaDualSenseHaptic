using System.Buffers.Binary;
using ForzaHaptics.Haptics;
using ForzaHaptics.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ForzaHaptics.Output;

public interface IHapticOutput : IDisposable
{
    string Description { get; }
    bool IsAlive { get; }
    void Start();
    /// <summary>Short status-line state (errors, counters), or empty.</summary>
    string Health { get; }
}

/// <summary>
/// A DualSense connected over USB is a 4-channel sound card: channels 1–2 are the speaker/headphones,
/// and channels 3–4 are the left and right haptic motors. PCM is written directly to channels 3–4
/// through WASAPI in shared mode, allowing other applications to play audio simultaneously.
/// </summary>
public sealed class UsbAudioOutput : IHapticOutput
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly WasapiOut _out;
    private readonly SourceWaveProvider _provider;
    private readonly MMDevice _device;
    private volatile bool _alive;

    public string Description { get; }
    public string Health => _provider.Error ?? "";
    public bool IsAlive => _alive;

    public UsbAudioOutput(MMDevice device, int latencyMs, int channelLeft, int channelRight, Func<int, IHapticSource> sourceFactory)
    {
        WaveFormat mix;
        using (var client = device.AudioClient)
        {
            mix = client.MixFormat;
        }

        if (mix.Channels < 4)
        {
            throw new InvalidOperationException(
                $"Audio device \"{device.FriendlyName}\" has {mix.Channels} channel(s), but haptics require 4. " +
                "Control Panel → Sound → DualSense → Configure → Quadraphonic.");
        }
        if (channelLeft >= mix.Channels || channelRight >= mix.Channels || channelLeft < 0 || channelRight < 0)
        {
            throw new InvalidOperationException($"UsbHapticChannels [{channelLeft}, {channelRight}] are outside the range 0..{mix.Channels - 1}");
        }

        var sampleFormat = DetectSampleFormat(mix);
        var source = sourceFactory(mix.SampleRate);
        _provider = new SourceWaveProvider(mix, sampleFormat, source, channelLeft, channelRight);

        Description = $"USB audio \"{device.FriendlyName}\": {mix.SampleRate} Hz, {mix.Channels} ch, " +
                      $"{mix.BitsPerSample}-bit ({sampleFormat}); haptics → channels {channelLeft + 1} (L) and {channelRight + 1} (R)";

        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        _out.PlaybackStopped += (_, e) =>
        {
            _alive = false;
            if (e.Exception != null) Log.Error($"USB audio stopped: {e.Exception.Message}");
        };
        try { _out.Init(_provider); }
        catch
        {
            _out.Dispose();
            throw;
        }

        try
        {
            var volume = device.AudioEndpointVolume;
            if (volume.Mute || volume.MasterVolumeLevelScalar < 0.9f)
            {
                Log.Warn($"DualSense device volume is {volume.MasterVolumeLevelScalar * 100f:0}%{(volume.Mute ? " (muted)" : "")}; " +
                         "it also affects haptics. 100% is recommended.");
            }
        }
        catch
        {
            // non-critical
        }
        // Keep the endpoint alive for WASAPI; ownership transfers only after construction succeeds.
        _device = device;
    }

    public void Start()
    {
        _alive = true;
        _out.Play();
    }

    public void Dispose()
    {
        _alive = false;
        try { _out.Stop(); } catch { /* ignore */ }
        try { _out.Dispose(); }
        finally { _device.Dispose(); }
    }

    /// <summary>Finds an active DualSense audio output (DualSense / DualSense Edge / "Wireless Controller").</summary>
    public static MMDevice? FindDualSenseEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (IsDualSenseName(SafeName(device))) return device;
            device.Dispose();
        }
        return null;
    }

    public static bool IsDualSenseName(string name) =>
        name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase);

    public static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; } catch { return "?"; }
    }

    public static string DescribeFormat(MMDevice device)
    {
        try
        {
            using var client = device.AudioClient;
            var f = client.MixFormat;
            return $"{f.SampleRate} Hz, {f.Channels} ch, {f.BitsPerSample}-bit";
        }
        catch (Exception ex)
        {
            return $"unknown format ({ex.Message})";
        }
    }

    private enum SampleFormat
    {
        Float32,
        Pcm16,
        Pcm24,
        Pcm32,
    }

    private static SampleFormat DetectSampleFormat(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = f.Encoding == WaveFormatEncoding.Pcm;
        if (f is WaveFormatExtensible ext)
        {
            isFloat |= ext.SubFormat == SubtypeIeeeFloat;
            isPcm |= ext.SubFormat == SubtypePcm;
        }

        if (isFloat && f.BitsPerSample == 32) return SampleFormat.Float32;
        if (isPcm)
        {
            switch (f.BitsPerSample)
            {
                case 16: return SampleFormat.Pcm16;
                case 24: return SampleFormat.Pcm24;
                case 32: return SampleFormat.Pcm32;
            }
        }
        throw new NotSupportedException($"Unsupported mixer format: {f.Encoding}, {f.BitsPerSample}-bit");
    }

    /// <summary>
    /// Supplies WASAPI with data in the device mixer's exact format (without Windows conversion),
    /// filling only the haptic channels.
    /// </summary>
    private sealed class SourceWaveProvider : IWaveProvider
    {
        private readonly SampleFormat _format;
        private readonly IHapticSource _source;
        private readonly int _chL, _chR, _bytesPerSample, _frameBytes;
        private float[] _left = Array.Empty<float>();
        private float[] _right = Array.Empty<float>();

        public WaveFormat WaveFormat { get; }
        public string? Error { get; private set; }

        public SourceWaveProvider(WaveFormat format, SampleFormat sampleFormat, IHapticSource source, int chL, int chR)
        {
            WaveFormat = format;
            _format = sampleFormat;
            _source = source;
            _chL = chL;
            _chR = chR;
            _bytesPerSample = format.BitsPerSample / 8;
            _frameBytes = _bytesPerSample * format.Channels;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / _frameBytes;
            var dst = buffer.AsSpan(offset, frames * _frameBytes);
            dst.Clear();
            if (frames == 0) return 0;

            if (_left.Length < frames)
            {
                _left = new float[frames];
                _right = new float[frames];
            }

            try
            {
                _source.Render(_left.AsSpan(0, frames), _right.AsSpan(0, frames));
            }
            catch (Exception ex)
            {
                // Do not let an exception terminate the audio thread; report it in status and return silence.
                Error = "synthesis error: " + ex.Message;
                return frames * _frameBytes;
            }

            for (int i = 0; i < frames; i++)
            {
                int frame = i * _frameBytes;
                WriteSample(dst, frame + _chL * _bytesPerSample, _left[i]);
                WriteSample(dst, frame + _chR * _bytesPerSample, _right[i]);
            }
            return frames * _frameBytes;
        }

        private void WriteSample(Span<byte> dst, int offset, float value)
        {
            value = MathX.Clamp(value, -1f, 1f);
            switch (_format)
            {
                case SampleFormat.Float32:
                    BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(offset, 4), value);
                    break;
                case SampleFormat.Pcm16:
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(offset, 2), (short)(value * 32767f));
                    break;
                case SampleFormat.Pcm24:
                    int v24 = (int)(value * 8388607f);
                    dst[offset] = (byte)v24;
                    dst[offset + 1] = (byte)(v24 >> 8);
                    dst[offset + 2] = (byte)(v24 >> 16);
                    break;
                case SampleFormat.Pcm32:
                    BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(offset, 4), (int)(value * 2147483520f));
                    break;
            }
        }
    }
}
