using ForzaHaptics.Haptics;

namespace ForzaHaptics.Tests;

public sealed class DspSafetyTests
{
    [Theory]
    [InlineData(3000)]
    [InlineData(48000)]
    public void MalformedTargetsCannotPoisonFollowingAudio(int sampleRate)
    {
        var bus = new HapticBus();
        var config = new AppConfig();
        var synth = new HapticSynth(bus, () => config, sampleRate);
        var targets = new HapticTargets
        {
            EngineAmp = float.NaN, EngineFreq = float.PositiveInfinity,
            RoadFreq = float.MaxValue, StripFreq = float.NegativeInfinity,
        };
        targets.Road[0] = float.NaN;
        targets.Strip[1] = float.PositiveInfinity;
        targets.Spin[0] = float.MaxValue;
        bus.Targets = targets;
        bus.Kick(new HapticKick(float.PositiveInfinity, 1f, float.MaxValue, 0.1f, KickKind.Noise));
        var left = new float[sampleRate / 10];
        var right = new float[left.Length];
        synth.Render(left, right);
        AssertFiniteAudio(left, right);

        bus.Targets = new HapticTargets { EngineAmp = 0.2f, EngineFreq = 120f };
        for (int i = 0; i < 10; i++)
        {
            synth.Render(left, right);
            AssertFiniteAudio(left, right);
        }
        Assert.Contains(left, value => MathF.Abs(value) > 0.01f);
        Assert.Contains("engine", synth.Meters.Active);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(48000)]
    public void ResetDiscardsQueuedAndActiveImpulses(int sampleRate)
    {
        var bus = new HapticBus();
        var synth = new HapticSynth(bus, () => new AppConfig(), sampleRate);
        var left = new float[sampleRate / 100];
        var right = new float[left.Length];
        bus.Kick(new HapticKick(0.8f, 0.8f, 110f, 0.5f, KickKind.Sine) { EffectName = "shift" });
        synth.Render(left, right);
        Assert.Contains(left, value => value != 0f);
        Assert.Contains("shift", synth.Meters.Active);
        long previousGeneration = bus.ResetGeneration;
        bus.ResetEffects();
        // A late stale enqueue must be rejected by the consumer too.
        bus.Kicks.Enqueue(new HapticKick(1f, 1f, 110f, 0.5f, KickKind.Sine) { Generation = previousGeneration });
        synth.Render(left, right);
        Assert.All(left, value => Assert.Equal(0f, value));
        Assert.All(right, value => Assert.Equal(0f, value));
        Assert.Empty(synth.Meters.Active);
    }

    [Fact]
    public void DspPrimitivesRecoverFromNonfiniteValuesAndBoundExtremeFrequencies()
    {
        var noise = new SmoothNoise(1);
        Assert.True(float.IsFinite(noise.Next(float.PositiveInfinity, 3000)));
        Assert.True(float.IsFinite(noise.Next(float.MaxValue, 3000)));
        Assert.True(float.IsFinite(noise.Next(120, 3000)));
        var filter = new Biquad();
        filter.SetLowPass(3000, float.NaN);
        Assert.Equal(0f, filter.Process(float.NaN));
        filter.SetLowPass(3000, 300);
        Assert.True(float.IsFinite(filter.Process(0.5f)));
        var compressor = new Compressor();
        compressor.Set(3000, float.NaN, float.PositiveInfinity, 0, 0);
        Assert.Equal(0f, compressor.Gain(float.NaN, 0));
        Assert.InRange(compressor.Gain(0.5f, 0.5f), 0f, 1f);
        var voices = new VoicePool(3000, 1);
        voices.Add(float.NaN, 120, 0.1f, false);
        voices.Add(1, float.PositiveInfinity, 0.1f, true);
        Assert.Equal(0f, voices.Next());
        voices.Add(1, float.MaxValue, 0.1f, true);
        for (int i = 0; i < 1000; i++) Assert.True(float.IsFinite(voices.Next()));
    }

    [Fact]
    public void StatusBecomesStaleAtTheOutputWatchdogDeadline()
    {
        var status = new TelemetryStatus(true, 123, 4500, 8000, 3, 0.2f, 60, 0);
        string current = HapticEngine.FormatTelemetryStatus(status, 0.29, "signal peaks L 0.1 R 0.1", "engine", "L2 off", 5310, false);
        Assert.Contains("123 km/h", current);
        Assert.Contains("signal peaks", current);
        Assert.Contains("command L2 off", current);
        string stale = HapticEngine.FormatTelemetryStatus(status, TelemetryProcessor.TimeoutSeconds, "", "", "", 5310, false);
        Assert.Contains("stale", stale);
        Assert.DoesNotContain("123", stale);
        Assert.DoesNotContain("4500", stale);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(48000)]
    public void SharedEngineToneHasIdenticalStereoChannels(int sampleRate)
    {
        var bus = new HapticBus { Targets = new HapticTargets { EngineAmp = 0.2f, EngineFreq = 120 } };
        var config = new AppConfig();
        var synth = new HapticSynth(bus, () => config, sampleRate);
        var left = new float[sampleRate / 10];
        var right = new float[left.Length];
        synth.Render(left, right);
        Assert.Equal(left, right);
        Assert.Equal(synth.Meters.PeakL, synth.Meters.PeakR);
        Assert.Contains(left, value => MathF.Abs(value) > 0.01f);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(48000)]
    public void ChannelSwapPreservesTheExactStereoSignalAndMeters(int sampleRate)
    {
        var targets = new HapticTargets { RoadFreq = 130 };
        targets.Road[0] = 0.1f;
        targets.Road[1] = 0.5f;
        var normalBus = new HapticBus { Targets = targets };
        var swappedBus = new HapticBus { Targets = targets };
        var normalConfig = new AppConfig();
        var swappedConfig = new AppConfig { SwapLeftRight = true };
        var normal = new HapticSynth(normalBus, () => normalConfig, sampleRate);
        var swapped = new HapticSynth(swappedBus, () => swappedConfig, sampleRate);
        var left = new float[sampleRate / 20];
        var right = new float[left.Length];
        var swappedLeft = new float[left.Length];
        var swappedRight = new float[left.Length];
        for (int i = 0; i < 4; i++)
        {
            normal.Render(left, right);
            swapped.Render(swappedLeft, swappedRight);
            Assert.Equal(left, swappedRight);
            Assert.Equal(right, swappedLeft);
            Assert.Equal(normal.Meters.PeakL, swapped.Meters.PeakR);
            Assert.Equal(normal.Meters.PeakR, swapped.Meters.PeakL);
        }
        Assert.Contains(left.Zip(right), pair => pair.First != pair.Second);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(48000)]
    public void SingleSideImpulseDoesNotLeakIntoOtherChannel(int sampleRate)
    {
        var bus = new HapticBus();
        var config = new AppConfig();
        var synth = new HapticSynth(bus, () => config, sampleRate);
        bus.Kick(new HapticKick(0.8f, 0, 110, 0.1f, KickKind.Sine));
        var left = new float[sampleRate / 20];
        var right = new float[left.Length];
        synth.Render(left, right);
        Assert.Contains(left, value => value != 0);
        Assert.All(right, value => Assert.Equal(0f, value));
        Assert.Equal(0, synth.Meters.PeakR);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(48000)]
    public void ResetAfterSnapshotReadCannotEmitTheOldBlock(int sampleRate)
    {
        var active = new HapticTargets { EngineAmp = 0.8f, EngineFreq = 120 };
        var bus = new HapticBus { Targets = active };
        var config = new AppConfig();
        bool resetDuringRender = false;
        var synth = new HapticSynth(bus, () =>
        {
            if (resetDuringRender) bus.ResetEffects();
            return config;
        }, sampleRate);
        var left = new float[sampleRate / 20];
        var right = new float[left.Length];
        synth.Render(left, right);
        Assert.Contains(left, value => value != 0);
        resetDuringRender = true;
        synth.Render(left, right);
        Assert.All(left, value => Assert.Equal(0f, value));
        Assert.All(right, value => Assert.Equal(0f, value));
        Assert.Empty(synth.Meters.Active);
        Assert.Same(HapticTargets.Silent, bus.ReadTargets(out long generation));
        Assert.Equal(bus.ResetGeneration, generation);
        resetDuringRender = false;
        synth.Render(left, right);
        Assert.All(left, value => Assert.Equal(0f, value));
    }

    private static void AssertFiniteAudio(params float[][] channels)
    {
        foreach (float[] channel in channels)
            Assert.All(channel, value => { Assert.True(float.IsFinite(value)); Assert.InRange(value, -1f, 1f); });
    }
}
