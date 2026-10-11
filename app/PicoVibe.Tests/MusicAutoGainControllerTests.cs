using PicoVibe.Core.Lighting;

namespace PicoVibe.Tests;

public sealed class MusicAutoGainControllerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstActiveSample_NormalizesBrowserAndLowLevelSystemPlayer()
    {
        var browser = new MusicAutoGainController();
        var systemPlayer = new MusicAutoGainController();

        var browserLevels = browser.Process(0.60, 0.30, Start);
        var systemLevels = systemPlayer.Process(0.18, 0.045, Start);

        Assert.Equal(0.32, browserLevels.Rms, 6);
        Assert.Equal(0.32, systemLevels.Rms, 6);
        Assert.InRange(systemPlayer.CurrentGain, 4.0, 4.5);
    }

    [Fact]
    public void DigitalSilenceAndSubFloorNoise_RemainBlackEvenAfterHighGain()
    {
        var controller = new MusicAutoGainController();
        _ = controller.Process(0.12, 0.025, Start);
        Assert.True(controller.CurrentGain > 6.0);

        var silence = controller.Process(0, 0, Start.AddMilliseconds(40));
        var noise = controller.Process(0.012, 0.004, Start.AddMilliseconds(80));

        Assert.Equal(AudioLevels.FromPeakAndRms(0, 0), silence);
        Assert.Equal(AudioLevels.FromPeakAndRms(0, 0), noise);
        Assert.True(controller.CurrentGain > 6.0);
    }

    [Fact]
    public void SustainedSilence_ResetsGainBeforeTheNextTrack()
    {
        var controller = new MusicAutoGainController();
        _ = controller.Process(0.12, 0.025, Start);
        Assert.True(controller.CurrentGain > 6.0);

        AudioLevels levels = AudioLevels.FromPeakAndRms(1, 1);
        for (var index = 1; index <= 40; index++)
        {
            levels = controller.Process(
                0,
                0,
                Start.AddMilliseconds(index * 40));
        }

        Assert.Equal(AudioLevels.FromPeakAndRms(0, 0), levels);
        Assert.Equal(1.0, controller.CurrentGain, 6);

        var nextTrack = controller.Process(0.18, 0.045, Start.AddSeconds(2));
        Assert.Equal(0.32, nextTrack.Rms, 6);
    }

    [Fact]
    public void PersistentPlayerLevelDrop_RebasesWithoutTreatingABriefDipAsANewSource()
    {
        var controller = new MusicAutoGainController();
        DateTimeOffset timestamp = Start;
        for (var index = 0; index < 25; index++)
        {
            _ = controller.Process(0.60, 0.30, timestamp);
            timestamp = timestamp.AddMilliseconds(40);
        }

        for (var index = 0; index < 100; index++)
        {
            _ = controller.Process(0.18, 0.045, timestamp);
            timestamp = timestamp.AddMilliseconds(40);
        }

        var settled = controller.Process(0.18, 0.045, timestamp);
        Assert.InRange(settled.Rms, 0.28, 0.34);
        Assert.InRange(controller.CurrentGain, 3.7, 4.5);
    }

    [Fact]
    public void LouderSource_ReducesGainQuicklyAndKeepsOutputBounded()
    {
        var controller = new MusicAutoGainController();
        DateTimeOffset timestamp = Start;
        _ = controller.Process(0.18, 0.045, timestamp);

        AudioLevels levels = AudioLevels.FromPeakAndRms(0, 0);
        for (var index = 1; index <= 25; index++)
        {
            timestamp = timestamp.AddMilliseconds(40);
            levels = controller.Process(0.60, 0.30, timestamp);
        }

        Assert.InRange(controller.CurrentGain, 1.0, 1.3);
        Assert.All(
            new[] { levels.Rms, levels.Bass, levels.Mid, levels.Treble },
            value => Assert.InRange(value, 0.0, 1.0));
    }

    [Fact]
    public void InvalidMeterValues_AreTreatedAsSilence()
    {
        var controller = new MusicAutoGainController();

        var levels = controller.Process(
            double.NaN,
            double.PositiveInfinity,
            Start);

        Assert.Equal(AudioLevels.FromPeakAndRms(0, 0), levels);
    }
}
