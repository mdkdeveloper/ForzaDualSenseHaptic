namespace ForzaHaptics.Output;

/// <summary>Simple 16-bit stereo WAV writer (left/right motor) for offline verification.</summary>
public sealed class WavWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly int _sampleRate;
    private long _frames;

    public double PeakL { get; private set; }
    public double PeakR { get; private set; }
    private double _sumSqL, _sumSqR;

    public WavWriter(string path, int sampleRate)
    {
        _sampleRate = sampleRate;
        _writer = new BinaryWriter(File.Create(path));
        WriteHeader(0);
    }

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            float l = Math.Clamp(left[i], -1f, 1f), r = Math.Clamp(right[i], -1f, 1f);
            _writer.Write((short)(l * 32767f));
            _writer.Write((short)(r * 32767f));
            PeakL = Math.Max(PeakL, Math.Abs(l));
            PeakR = Math.Max(PeakR, Math.Abs(r));
            _sumSqL += l * l;
            _sumSqR += r * r;
        }
        _frames += left.Length;
    }

    public double RmsL => _frames == 0 ? 0 : Math.Sqrt(_sumSqL / _frames);
    public double RmsR => _frames == 0 ? 0 : Math.Sqrt(_sumSqR / _frames);
    public double Seconds => _frames / (double)_sampleRate;

    private void WriteHeader(long frames)
    {
        const short channels = 2, bits = 16;
        int blockAlign = channels * bits / 8;
        long dataBytes = frames * blockAlign;
        _writer.Seek(0, SeekOrigin.Begin);
        _writer.Write("RIFF"u8);
        _writer.Write((uint)(36 + dataBytes));
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);
        _writer.Write((short)1);
        _writer.Write(channels);
        _writer.Write(_sampleRate);
        _writer.Write(_sampleRate * blockAlign);
        _writer.Write((short)blockAlign);
        _writer.Write(bits);
        _writer.Write("data"u8);
        _writer.Write((uint)dataBytes);
    }

    public void Dispose()
    {
        WriteHeader(_frames);
        _writer.Dispose();
    }
}
