using PicoVibe.Core.Configuration;
using PicoVibe.Core.Lighting;

namespace PicoVibe.Tests;

public sealed class FrameRendererTests
{
    public static IEnumerable<object[]> SystemVisualizers =>
        Enum.GetValues<SystemVisualizer>().Select(value => new object[] { value });

    public static IEnumerable<object[]> MusicVisualizers =>
        Enum.GetValues<MusicVisualizer>().Select(value => new object[] { value });

    [Fact]
    public void SystemGradientBar_RendersFractionalLevelAndGlobalBrightness()
    {
        var options = new SystemFrameOptions
        {
            Visualizer = SystemVisualizer.GradientBar,
            LowColor = new RgbColor(0, 200, 0),
            HighColor = new RgbColor(200, 0, 0),
        };

        var frame = SystemFrameRenderer.Render(0.5, options, new FrameRenderContext(12, 50));

        Assert.Equal(36, frame.Length);
        Assert.Contains(frame[..18], value => value > 0);
        Assert.All(frame[18..], value => Assert.Equal(0, value));
        Assert.All(frame, value => Assert.InRange(value, (byte)0, (byte)100));
    }

    [Fact]
    public void FrameRenderers_GlobalZeroTurnsEverythingOff()
    {
        var system = SystemFrameRenderer.Render(
            1,
            new SystemFrameOptions { Visualizer = SystemVisualizer.Pulse },
            new FrameRenderContext(12, 0));
        var music = MusicFrameRenderer.Render(
            new AudioLevels(1, 1, 1, 1),
            new MusicFrameOptions { Visualizer = MusicVisualizer.RainbowBeat },
            new FrameRenderContext(12, 0, 0.3));

        Assert.All(system, value => Assert.Equal(0, value));
        Assert.All(music, value => Assert.Equal(0, value));
    }

    [Fact]
    public void MusicSpectrum_InterpolatesAnyBinCountToLedCount()
    {
        var levels = new AudioLevels(0.4, 0.8, 0.3, 0.1, new[] { 0.0, 0.5, 1.0 });
        var options = new MusicFrameOptions
        {
            Visualizer = MusicVisualizer.Spectrum,
            Sensitivity = 1,
            NoiseGate = 0,
        };

        var frame = MusicFrameRenderer.Render(levels, options, new FrameRenderContext(12, 100));

        Assert.Equal(36, frame.Length);
        Assert.Equal(new byte[] { 0, 0, 0 }, frame[..3]);
        Assert.Contains(frame[^3..], value => value > 0);
    }

    [Fact]
    public void PeakAndRmsMapping_CompensatesHighDynamicRangePlayers()
    {
        var browser = AudioLevels.FromPeakAndRms(0.6, 0.30);
        var systemPlayer = AudioLevels.FromPeakAndRms(0.6, 0.08);

        Assert.Equal(0.30, browser.Rms, 6);
        Assert.Equal(0.252, systemPlayer.Rms, 6);
        Assert.InRange(systemPlayer.Rms / browser.Rms, 0.80, 1.0);
        Assert.InRange(systemPlayer.Bass / systemPlayer.Rms, 1.0, 1.5);
        Assert.InRange(systemPlayer.Treble / systemPlayer.Rms, 1.0, 1.5);
    }

    [Fact]
    public void PeakAndRmsMapping_LeavesSilenceBlack()
    {
        var levels = AudioLevels.FromPeakAndRms(0, 0);
        var frame = MusicFrameRenderer.Render(
            levels,
            new MusicFrameOptions { Visualizer = MusicVisualizer.Aurora, NoiseGate = 0.01 },
            new FrameRenderContext(12, 100, 0.2));

        Assert.All(frame, value => Assert.Equal(0, value));
    }

    [Theory]
    [MemberData(nameof(MusicVisualizers))]
    public void EveryMusicVisualizer_BalancesBrowserAndSystemPlayerLevels(
        MusicVisualizer visualizer)
    {
        var options = new MusicFrameOptions
        {
            Visualizer = visualizer,
            LowColor = new RgbColor(255, 255, 255),
            MidColor = new RgbColor(255, 255, 255),
            HighColor = new RgbColor(255, 255, 255),
            NoiseGate = 0,
        };
        var context = new FrameRenderContext(12, 100, 0.31);
        var browser = MusicFrameRenderer.Render(
            AudioLevels.FromPeakAndRms(0.6, 0.30), options, context);
        var systemPlayer = MusicFrameRenderer.Render(
            AudioLevels.FromPeakAndRms(0.6, 0.08), options, context);
        var browserEnergy = browser.Sum(value => (long)value);
        var systemEnergy = systemPlayer.Sum(value => (long)value);

        Assert.True(browserEnergy > 0);
        Assert.InRange(systemEnergy / (double)browserEnergy, 0.70, 1.10);
    }

    [Theory]
    [MemberData(nameof(MusicVisualizers))]
    public void EveryMusicVisualizer_BalancesOverallPlayerLevelDifferences(
        MusicVisualizer visualizer)
    {
        var options = new MusicFrameOptions
        {
            Visualizer = visualizer,
            LowColor = new RgbColor(255, 255, 255),
            MidColor = new RgbColor(255, 255, 255),
            HighColor = new RgbColor(255, 255, 255),
            NoiseGate = 0,
        };
        var context = new FrameRenderContext(12, 100, 0.31);
        var browserController = new MusicAutoGainController();
        var systemController = new MusicAutoGainController();
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var browser = MusicFrameRenderer.Render(
            browserController.Process(0.60, 0.30, timestamp), options, context);
        var systemPlayer = MusicFrameRenderer.Render(
            systemController.Process(0.18, 0.045, timestamp), options, context);
        var browserEnergy = browser.Sum(value => (long)value);
        var systemEnergy = systemPlayer.Sum(value => (long)value);

        Assert.True(browserEnergy > 0);
        Assert.InRange(systemEnergy / (double)browserEnergy, 0.85, 1.20);
    }

    /// <summary>
    /// The centre-mirror effect throws the same pulse out to both sides, so a
    /// linear palette would colour its two arms differently.  Its colour has to
    /// mirror with its intensity.
    /// </summary>
    [Fact]
    public void MusicCenterMirror_KeepsBothArmsTheSameColour()
    {
        var frame = MusicFrameRenderer.Render(
            new AudioLevels(1.0, 1.0, 1.0, 1.0),
            new MusicFrameOptions
            {
                Visualizer = MusicVisualizer.CenterMirror,
                LowColor = new RgbColor(255, 0, 0),
                MidColor = new RgbColor(128, 0, 128),
                HighColor = new RgbColor(0, 0, 255),
                NoiseGate = 0,
                Sensitivity = 5.0,
            },
            new FrameRenderContext(12, 100));

        for (var led = 0; led < 6; led++)
        {
            var mirrored = 11 - led;
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.Equal(frame[(led * 3) + channel], frame[(mirrored * 3) + channel]);
            }
        }

        // Ends take the low stop, the centre the high one.
        Assert.True(frame[0] > frame[2], "The outer lamp should lean towards the low colour.");
        Assert.True(frame[(5 * 3) + 2] > frame[5 * 3], "The centre should lean towards the high colour.");
    }

    [Fact]
    public void MusicIntensity_PrecompensatesFirmwareGamma()
    {
        var frame = MusicFrameRenderer.Render(
            new AudioLevels(0.25, 0.25, 0.25, 0.25),
            new MusicFrameOptions
            {
                Visualizer = MusicVisualizer.BassPulse,
                // Bass reads the low stop: the palette now runs primary to
                // secondary end to end, so the high stop is the other colour.
                LowColor = new RgbColor(255, 255, 255),
                NoiseGate = 0,
            },
            new FrameRenderContext(1, 100));

        Assert.Equal(new byte[] { 128, 128, 128 }, frame);
    }

    [Fact]
    public void SystemRenderer_ReverseMirrorsPhysicalPixels()
    {
        var forward = SystemFrameRenderer.Render(
            0.25,
            new SystemFrameOptions { Visualizer = SystemVisualizer.Bar, Reverse = false },
            new FrameRenderContext(4, 100));
        var reverse = SystemFrameRenderer.Render(
            0.25,
            new SystemFrameOptions { Visualizer = SystemVisualizer.Bar, Reverse = true },
            new FrameRenderContext(4, 100));

        Assert.Equal(forward[..3], reverse[^3..]);
        Assert.Equal(forward[^3..], reverse[..3]);
    }

    [Fact]
    public void Renderers_KeepEachProfileInsideItsOwnLedSegment()
    {
        var context = new FrameRenderContext(8, 100, Start: 2, Count: 3);
        var system = SystemFrameRenderer.Render(
            1,
            new SystemFrameOptions { Visualizer = SystemVisualizer.Pulse },
            context);
        var music = MusicFrameRenderer.Render(
            new AudioLevels(1, 1, 1, 1),
            new MusicFrameOptions { Visualizer = MusicVisualizer.BassPulse, NoiseGate = 0 },
            context);

        Assert.Equal(24, system.Length);
        Assert.Equal(24, music.Length);
        Assert.All(system[..6], value => Assert.Equal(0, value));
        Assert.All(system[15..], value => Assert.Equal(0, value));
        Assert.All(music[..6], value => Assert.Equal(0, value));
        Assert.All(music[15..], value => Assert.Equal(0, value));
        Assert.Contains(system[6..15], value => value > 0);
        Assert.Contains(music[6..15], value => value > 0);
    }

    [Theory]
    [MemberData(nameof(SystemVisualizers))]
    public void EverySystemVisualizer_RendersACompleteVisibleFrame(SystemVisualizer visualizer)
    {
        var frame = SystemFrameRenderer.Render(
            0.65,
            new SystemFrameOptions { Visualizer = visualizer },
            new FrameRenderContext(12, 100, 0.31));

        Assert.Equal(36, frame.Length);
        Assert.Contains(frame, value => value > 0);
    }

    [Theory]
    [MemberData(nameof(MusicVisualizers))]
    public void EveryMusicVisualizer_RendersACompleteVisibleFrame(MusicVisualizer visualizer)
    {
        var frame = MusicFrameRenderer.Render(
            new AudioLevels(0.55, 0.8, 0.45, 0.7, new[] { 0.2, 0.7, 0.4, 0.9 }),
            new MusicFrameOptions { Visualizer = visualizer, NoiseGate = 0 },
            new FrameRenderContext(12, 100, 0.31));

        Assert.Equal(36, frame.Length);
        Assert.Contains(frame, value => value > 0);
    }

    [Theory]
    [MemberData(nameof(MusicVisualizers))]
    public void EveryMusicVisualizer_IsBlackBelowNoiseGate(MusicVisualizer visualizer)
    {
        var frame = MusicFrameRenderer.Render(
            new AudioLevels(0.01, 0.01, 0.01, 0.01, new[] { 0.01, 0.01, 0.01 }),
            new MusicFrameOptions { Visualizer = visualizer, NoiseGate = 0.05 },
            new FrameRenderContext(12, 100, 0.31));

        Assert.All(frame, value => Assert.Equal(0, value));
    }

    [Fact]
    public void SystemCenterBar_ExpandsSymmetrically()
    {
        var frame = SystemFrameRenderer.Render(
            0.5,
            new SystemFrameOptions
            {
                Visualizer = SystemVisualizer.CenterBar,
                LowColor = new RgbColor(255, 255, 255),
                HighColor = new RgbColor(255, 255, 255),
            },
            new FrameRenderContext(12, 100));

        for (var index = 0; index < 6; index++)
        {
            Assert.Equal(frame[index * 3], frame[(11 - index) * 3]);
        }
    }

    [Fact]
    public void SystemColorWash_UsesOneMetricColorAcrossTheStrip()
    {
        var frame = SystemFrameRenderer.Render(
            0.5,
            new SystemFrameOptions
            {
                Visualizer = SystemVisualizer.ColorWash,
                LowColor = new RgbColor(0, 200, 0),
                HighColor = new RgbColor(200, 0, 0),
            },
            new FrameRenderContext(4, 100));

        Assert.Equal(new byte[] { 100, 100, 0 }, frame[..3]);
        for (var index = 1; index < 4; index++)
        {
            Assert.Equal(frame[..3], frame[(index * 3)..((index + 1) * 3)]);
        }
    }

    [Theory]
    [InlineData(SystemVisualizer.Breathing)]
    public void AnimatedSystemVisualizers_ChangeWithPhase(SystemVisualizer visualizer)
    {
        var options = new SystemFrameOptions { Visualizer = visualizer };
        var first = SystemFrameRenderer.Render(0.7, options, new FrameRenderContext(12, 100, 0));
        var second = SystemFrameRenderer.Render(0.7, options, new FrameRenderContext(12, 100, 0.5));

        Assert.False(first.SequenceEqual(second));
    }

    [Theory]
    [InlineData(MusicVisualizer.BeatChase)]
    [InlineData(MusicVisualizer.Wave)]
    [InlineData(MusicVisualizer.Aurora)]
    public void AnimatedMusicVisualizers_ChangeWithPhase(MusicVisualizer visualizer)
    {
        var levels = new AudioLevels(0.55, 0.8, 0.45, 0.7);
        var options = new MusicFrameOptions { Visualizer = visualizer, NoiseGate = 0 };
        var first = MusicFrameRenderer.Render(levels, options, new FrameRenderContext(12, 100, 0.1));
        var second = MusicFrameRenderer.Render(levels, options, new FrameRenderContext(12, 100, 0.35));

        Assert.False(first.SequenceEqual(second));
    }

    [Theory]
    [InlineData(30, 30, 90, 0)]
    [InlineData(60, 30, 90, 0.5)]
    [InlineData(120, 30, 90, 1)]
    [InlineData(50, 50, 50, 0)]
    public void SystemRenderer_NormalizesConfiguredDisplayRange(
        double value,
        double minimum,
        double maximum,
        double expected)
    {
        Assert.Equal(expected, SystemFrameRenderer.NormalizeValue(value, minimum, maximum), 6);
    }
}
