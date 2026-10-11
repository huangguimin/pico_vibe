using PicoVibe.Core.Configuration;
using PicoVibe.Core.Lighting;

namespace PicoVibe.Tests;

public sealed class RgbIniCommandBuilderTests
{
    [Fact]
    public void Build_AppliesGlobalBrightnessAndSegmentBounds()
    {
        var profile = new LightProfile
        {
            Effect = RgbEffect.Gradient,
            Primary = new RgbColor(300, -1, 40),
            Secondary = new RgbColor(0, 20, 255),
            Brightness = 101,
            Speed = 0,
            Start = 10,
            Count = 12,
            Reverse = true,
            Gamma = true,
            MaxMilliamps = 200,
        };

        var command = RgbIniCommandBuilder.Build(profile, 50, 12);

        Assert.Equal(
            "MODE=gradient,R=255,G=0,B=40,BR=51,SPD=1,DIR=1,START=10,N=2," +
            "GAMMA=1,MAXMA=200,R2=0,G2=20,B2=255",
            command);
    }

    [Fact]
    public void Build_EmitsBlinkAndPaletteSpecificFieldsOnly()
    {
        var blink = new LightProfile
        {
            Effect = RgbEffect.Blink,
            OnMilliseconds = 90,
            OffMilliseconds = 110,
            Pulses = 2,
            GapMilliseconds = 700,
            Fade = true,
        };
        var blinkCommand = RgbIniCommandBuilder.Build(blink, 100);
        Assert.Contains("ON=90,OFF=110,PULSES=2,GAP=700,FADE=1", blinkCommand);
        Assert.DoesNotContain("PAL=", blinkCommand);

        var palette = new LightProfile { Effect = RgbEffect.Palette, Palette = 3 };
        var paletteCommand = RgbIniCommandBuilder.Build(palette, 0);
        Assert.Contains("MODE=palette", paletteCommand);
        Assert.Contains("BR=0", paletteCommand);
        Assert.EndsWith("PAL=3", paletteCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GlobalSettingsTurnsDisabledLightsOff()
    {
        var profile = new LightProfile { Brightness = 200 };
        var global = new GlobalSettings
        {
            LightsEnabled = false,
            BrightnessPercent = 100,
            LedCount = 9,
        };

        var command = RgbIniCommandBuilder.Build(profile, global);

        Assert.Contains("BR=0", command);
        Assert.Contains("N=9", command);
    }

    [Theory]
    [InlineData(100, 63, 100)]
    [InlineData(200, 50, 128)]
    [InlineData(255, 100, 255)]
    [InlineData(-1, 100, 0)]
    public void ApplyBrightnessCeiling_PreservesProfileIndependence(
        int local,
        int global,
        int expected)
    {
        Assert.Equal(expected, RgbIniCommandBuilder.ApplyBrightnessCeiling(local, global));
    }

    [Theory]
    [InlineData(255, 100, 255)]
    [InlineData(255, 1, 3)]
    [InlineData(101, 50, 51)]
    [InlineData(-1, 100, 0)]
    public void ScaleBrightness_ClampsAndRounds(int local, int global, int expected)
    {
        Assert.Equal(expected, RgbIniCommandBuilder.ScaleBrightness(local, global));
    }
}
