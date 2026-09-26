using ForzaHaptics.Util;

namespace ForzaHaptics.Haptics;

/// <summary>
/// Test signal for checking the motors without the game (8-second cycle):
/// left motor → right motor → frequency sweep on both → alternating pulses.
/// </summary>
public sealed class TestPattern : IHapticSource
{
    private readonly float _fs;
    private long _sample;
    private float _phase;

    public volatile string Step = "";

    public TestPattern(int sampleRate)
    {
        _fs = sampleRate;
    }

    public void Render(Span<float> left, Span<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++, _sample++)
        {
            float t = CycleTime();
            float l = 0f, r = 0f;

            if (t < 3f)
            {
                _phase += 150f / _fs;
                float v = 0.6f * MathF.Sin(MathX.TwoPi * _phase);
                if (t < 1.5f) l = v; else r = v;
            }
            else if (t < 6f)
            {
                _phase += SweepFreq(t) / _fs;
                l = r = 0.5f * MathF.Sin(MathX.TwoPi * _phase);
            }
            else
            {
                float local = (t - 6f) % 0.5f;
                bool isLeft = ((int)((t - 6f) / 0.5f) & 1) == 0;
                _phase += 150f / _fs;
                float v = 0.8f * MathF.Exp(-local / 0.05f) * MathF.Sin(MathX.TwoPi * _phase);
                if (isLeft) l = v; else r = v;
            }

            if (_phase >= 1f) _phase -= 1f;
            left[i] = l;
            right[i] = r;
        }

        float now = CycleTime();
        Step = now < 1.5f ? "1/4 LEFT motor only, 150 Hz"
            : now < 3f ? "2/4 RIGHT motor only, 150 Hz"
            : now < 6f ? $"3/4 both-motor sweep, {SweepFreq(now),3:0} Hz"
            : "4/4 pulses: alternating left / right";
    }

    private float CycleTime() => (float)(_sample / (double)_fs % 8.0);

    // logarithmic sweep from 20 → 400 Hz over 3 seconds
    private static float SweepFreq(float t) => 20f * MathF.Pow(20f, (t - 3f) / 3f);
}
