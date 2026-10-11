using System.Globalization;
using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Lighting;

/// <summary>Serializes a light profile into the firmware's one-line RGB.INI format.</summary>
public static class RgbIniCommandBuilder
{
    public static string Build(LightProfile profile, GlobalSettings global)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(profile);
        var brightness = global.LightsEnabled
            ? ApplyBrightnessCeiling(profile.Brightness, global.BrightnessPercent)
            : 0;
        return BuildCore(profile, brightness, global.LedCount);
    }

    public static string Build(
        LightProfile profile,
        int globalBrightnessPercent,
        int ledCount = 12)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return BuildCore(
            profile,
            ScaleBrightness(profile.Brightness, globalBrightnessPercent),
            ledCount);
    }

    private static string BuildCore(LightProfile profile, int brightness, int ledCount)
    {
        var available = Math.Clamp(ledCount, 1, 64);
        var start = Math.Clamp(profile.Start, 0, available - 1);
        var count = Math.Clamp(profile.Count, 1, available - start);
        var primary = Clamp(profile.Primary);
        var secondary = Clamp(profile.Secondary);

        var fields = new List<string>(20)
        {
            $"MODE={EffectName(profile.Effect)}",
            $"R={primary.R}",
            $"G={primary.G}",
            $"B={primary.B}",
            $"BR={brightness}",
            $"SPD={Math.Clamp(profile.Speed, 1, 255)}",
            $"DIR={(profile.Reverse ? 1 : 0)}",
            $"START={start}",
            $"N={count}",
            $"GAMMA={(profile.Gamma ? 1 : 0)}",
            $"MAXMA={Math.Clamp(profile.MaxMilliamps, 0, 65_535)}",
        };

        if (profile.Effect == RgbEffect.Gradient)
        {
            fields.Add($"R2={secondary.R}");
            fields.Add($"G2={secondary.G}");
            fields.Add($"B2={secondary.B}");
        }

        if (profile.Effect == RgbEffect.Blink)
        {
            fields.Add($"ON={Math.Clamp(profile.OnMilliseconds, 0, 65_535)}");
            fields.Add($"OFF={Math.Clamp(profile.OffMilliseconds, 0, 65_535)}");
            fields.Add($"PULSES={Math.Clamp(profile.Pulses, 1, 255)}");
            fields.Add($"GAP={Math.Clamp(profile.GapMilliseconds, 0, 65_535)}");
            fields.Add($"FADE={(profile.Fade ? 1 : 0)}");
        }

        if (profile.Effect == RgbEffect.Palette)
        {
            fields.Add($"PAL={Math.Clamp(profile.Palette, 0, 3)}");
        }

        return string.Join(',', fields);
    }

    public static int ScaleBrightness(int profileBrightness, int globalBrightnessPercent)
    {
        var local = Math.Clamp(profileBrightness, 0, 255);
        var global = Math.Clamp(globalBrightnessPercent, 0, 100);
        return (int)Math.Round(
            local * global / 100.0,
            MidpointRounding.AwayFromZero);
    }

    public static int ApplyBrightnessCeiling(int profileBrightness, int globalBrightnessPercent)
    {
        var local = Math.Clamp(profileBrightness, 0, 255);
        var ceiling = (int)Math.Round(
            Math.Clamp(globalBrightnessPercent, 0, 100) * 255 / 100.0,
            MidpointRounding.AwayFromZero);
        return Math.Min(local, ceiling);
    }

    private static string EffectName(RgbEffect effect) => effect switch
    {
        RgbEffect.RainbowCycle => "rainbow_cycle",
        _ => effect.ToString().ToLower(CultureInfo.InvariantCulture),
    };

    private static RgbColor Clamp(RgbColor color) => new(
        Math.Clamp(color.R, 0, 255),
        Math.Clamp(color.G, 0, 255),
        Math.Clamp(color.B, 0, 255));
}
