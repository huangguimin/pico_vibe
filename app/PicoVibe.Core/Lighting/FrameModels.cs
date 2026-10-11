using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Lighting;

public readonly record struct FrameRenderContext(
    int LedCount,
    int GlobalBrightnessPercent,
    double Phase = 0,
    int Start = 0,
    int Count = 0)
{
    public int SegmentStart => Math.Clamp(Start, 0, Math.Max(0, LedCount - 1));

    public int SegmentCount => Math.Clamp(
        Count <= 0 ? LedCount - SegmentStart : Count,
        1,
        Math.Max(1, LedCount - SegmentStart));

    public static FrameRenderContext From(GlobalSettings global, double phase = 0)
    {
        ArgumentNullException.ThrowIfNull(global);
        return new FrameRenderContext(
            global.LedCount,
            global.LightsEnabled ? global.BrightnessPercent : 0,
            phase);
    }

    public static FrameRenderContext FromProfile(
        int physicalLedCount,
        LightProfile profile,
        double phase = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new FrameRenderContext(
            physicalLedCount,
            100,
            phase,
            profile.Start,
            profile.Count);
    }
}

public sealed class SystemFrameOptions
{
    public SystemVisualizer Visualizer { get; set; } = SystemVisualizer.GradientBar;
    public RgbColor LowColor { get; set; } = new(0, 255, 72);
    public RgbColor HighColor { get; set; } = new(255, 24, 0);
    public RgbColor BackgroundColor { get; set; } = RgbColor.Black;
    public bool Reverse { get; set; }
}

public sealed class MusicFrameOptions
{
    public MusicVisualizer Visualizer { get; set; } = MusicVisualizer.Spectrum;
    public RgbColor LowColor { get; set; } = new(0, 96, 255);
    public RgbColor MidColor { get; set; } = new(180, 0, 255);
    public RgbColor HighColor { get; set; } = new(255, 32, 80);
    public RgbColor BackgroundColor { get; set; } = RgbColor.Black;
    public double Sensitivity { get; set; } = 1.0;
    public double NoiseGate { get; set; } = 0.025;
    public bool Reverse { get; set; }
}

/// <summary>Normalized audio features, each expected in the range 0..1.</summary>
public sealed record AudioLevels(
    double Rms,
    double Bass,
    double Mid,
    double Treble,
    IReadOnlyList<double>? Spectrum = null)
{
    /// <summary>
    /// Maps a stereo output meter into stable pseudo-bands.  System players
    /// such as Apple Music can have a much larger peak-to-RMS ratio than a
    /// browser player (especially with loudness normalization or Atmos).
    /// Using peak as two bands and RMS as the others made visualizers disagree
    /// wildly about the brightness of the same audible output.
    /// </summary>
    public static AudioLevels FromPeakAndRms(double peak, double rms)
    {
        peak = FrameColorMath.Clamp01(peak);
        rms = FrameColorMath.Clamp01(rms);

        // Keep musical dynamics, but do not let a high-crest-factor mix make
        // every RMS-driven effect disappear.  The small band offsets retain
        // motion without pretending that a two-value meter is a real FFT.
        var body = Math.Max(rms, peak * 0.42);
        var transient = Math.Max(0.0, peak - body);
        var bass = FrameColorMath.Clamp01(body + (transient * 0.20));
        var treble = FrameColorMath.Clamp01(body + (transient * 0.30));
        return new AudioLevels(body, bass, body, treble);
    }
}
