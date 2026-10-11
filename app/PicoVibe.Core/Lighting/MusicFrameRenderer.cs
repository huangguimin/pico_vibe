using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Lighting;

/// <summary>Renders pre-analyzed audio levels into one RGB888 frame.</summary>
public static class MusicFrameRenderer
{
    public static byte[] Render(
        AudioLevels levels,
        MusicFrameOptions options,
        FrameRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(options);
        var physicalCount = Math.Clamp(context.LedCount, 1, 64);
        var start = Math.Clamp(context.SegmentStart, 0, physicalCount - 1);
        var count = Math.Clamp(context.SegmentCount, 1, physicalCount - start);
        var frame = new byte[physicalCount * 3];
        var sensitivity = Math.Clamp(options.Sensitivity, 0.1, 5.0);
        var rms = Gated(levels.Rms * sensitivity, options.NoiseGate);
        var bass = Gated(levels.Bass * sensitivity, options.NoiseGate);
        var mid = Gated(levels.Mid * sensitivity, options.NoiseGate);
        var treble = Gated(levels.Treble * sensitivity, options.NoiseGate);

        for (var index = 0; index < count; index++)
        {
            var position = count == 1 ? 0.0 : index / (double)(count - 1);
            var color = Gradient(options, position);
            var intensity = options.Visualizer switch
            {
                MusicVisualizer.VuMeter => Math.Clamp((rms * count) - index, 0.0, 1.0),
                MusicVisualizer.Spectrum => SpectrumAt(levels, index, count, sensitivity, options.NoiseGate),
                MusicVisualizer.CenterMirror => CenterMirrorIntensity(index, count, rms),
                MusicVisualizer.BassPulse => bass,
                MusicVisualizer.RainbowBeat => Math.Max(rms, bass),
                MusicVisualizer.BandSplit => BandSplitIntensity(index, count, bass, mid, treble),
                MusicVisualizer.BeatChase => BeatChaseIntensity(
                    index,
                    count,
                    context.Phase,
                    Math.Max(rms, bass)),
                MusicVisualizer.Wave => WaveIntensity(
                    position,
                    context.Phase,
                    Math.Max(rms, Math.Max(bass, Math.Max(mid, treble)))),
                MusicVisualizer.Aurora => AuroraIntensity(
                    position,
                    context.Phase,
                    bass,
                    mid,
                    treble),
                _ => 0.0,
            };
            if (options.Visualizer == MusicVisualizer.RainbowBeat)
            {
                color = FrameColorMath.Hsv(position + context.Phase, 1.0, 1.0);
            }
            else if (options.Visualizer == MusicVisualizer.BassPulse)
            {
                // Bass belongs at the low end of the palette.  It used to read
                // the high stop, which only looked right while both ends held
                // the same colour.
                color = options.LowColor;
            }
            else if (options.Visualizer == MusicVisualizer.CenterMirror)
            {
                // This one's intensity mirrors about the centre, so its palette
                // has to mirror too: a linear ramp would give the two arms of
                // the same pulse different colours.  Ends take the low stop,
                // the centre the high one.
                color = Gradient(options, MirroredPosition(position));
            }
            else if (options.Visualizer == MusicVisualizer.BandSplit)
            {
                color = BandSplitColor(index, count, options);
            }
            else if (options.Visualizer is MusicVisualizer.BeatChase or MusicVisualizer.Aurora)
            {
                color = Gradient(options, Wrap01(position + context.Phase));
            }

            var destination = start + (options.Reverse ? count - index - 1 : index);
            FrameColorMath.Write(
                frame,
                destination,
                physicalCount,
                false,
                color,
                options.BackgroundColor,
                PerceptualIntensity(intensity),
                context.GlobalBrightnessPercent);
        }

        return frame;
    }

    private static double SpectrumAt(
        AudioLevels levels,
        int index,
        int ledCount,
        double sensitivity,
        double noiseGate)
    {
        if (levels.Spectrum is { Count: > 0 } spectrum)
        {
            var sourcePosition = ledCount == 1
                ? 0.0
                : index * (spectrum.Count - 1.0) / (ledCount - 1.0);
            var lower = (int)Math.Floor(sourcePosition);
            var upper = Math.Min(spectrum.Count - 1, lower + 1);
            var amount = sourcePosition - lower;
            var value = spectrum[lower] + ((spectrum[upper] - spectrum[lower]) * amount);
            return Gated(value * sensitivity, noiseGate);
        }

        var third = index * 3 / Math.Max(1, ledCount);
        var band = third switch
        {
            0 => levels.Bass,
            1 => levels.Mid,
            _ => levels.Treble,
        };
        return Gated(band * sensitivity, noiseGate);
    }

    private static double CenterMirrorIntensity(int index, int ledCount, double value)
    {
        var center = (ledCount - 1) / 2.0;
        var distance = Math.Abs(index - center);
        var maxDistance = Math.Max(0.5, Math.Ceiling(ledCount / 2.0));
        return Math.Clamp((value * maxDistance) - distance + 0.5, 0.0, 1.0);
    }

    private static double BandSplitIntensity(
        int index,
        int ledCount,
        double bass,
        double mid,
        double treble) =>
        BandIndex(index, ledCount) switch
        {
            0 => bass,
            1 => mid,
            _ => treble,
        };

    private static RgbColor BandSplitColor(
        int index,
        int ledCount,
        MusicFrameOptions options) =>
        BandIndex(index, ledCount) switch
        {
            0 => options.LowColor,
            1 => options.MidColor,
            _ => options.HighColor,
        };

    private static int BandIndex(int index, int ledCount) =>
        Math.Min(2, index * 3 / Math.Max(1, ledCount));

    private static double BeatChaseIntensity(
        int index,
        int ledCount,
        double phase,
        double energy)
    {
        if (energy <= 0) return 0;
        var head = Wrap01(phase) * ledCount;
        var distanceBehind = (head - index + ledCount) % ledCount;
        var tailLength = Math.Clamp(ledCount / 3.0, 2.0, 5.0);
        return energy * Math.Max(0.0, 1.0 - (distanceBehind / tailLength));
    }

    private static double WaveIntensity(double position, double phase, double energy)
    {
        if (energy <= 0) return 0;
        var wave = 0.5 + (0.5 * Math.Sin((position * 2.0 - Wrap01(phase)) * Math.PI * 2.0));
        return energy * (0.12 + (0.88 * wave));
    }

    private static double AuroraIntensity(
        double position,
        double phase,
        double bass,
        double mid,
        double treble)
    {
        var bassWeight = Math.Pow(1.0 - position, 2.0);
        var midWeight = 1.0 - Math.Abs((position * 2.0) - 1.0);
        var trebleWeight = Math.Pow(position, 2.0);
        var weight = Math.Max(0.001, bassWeight + midWeight + trebleWeight);
        var level = ((bass * bassWeight) + (mid * midWeight) + (treble * trebleWeight)) / weight;
        var shimmer = 0.5 + (0.5 * Math.Sin((position + Wrap01(phase)) * Math.PI * 2.0));
        return FrameColorMath.Clamp01(level * (0.3 + (0.7 * shimmer)));
    }

    /// <summary>0 at both ends, 1 at the centre.</summary>
    private static double MirroredPosition(double position) =>
        1.0 - Math.Abs((position * 2.0) - 1.0);

    private static double Wrap01(double value) =>
        double.IsFinite(value) ? value - Math.Floor(value) : 0.0;

    private static double PerceptualIntensity(double value)
    {
        var normalized = FrameColorMath.Clamp01(value);
        // Host frames are gamma-corrected by the firmware.  Pre-compensating
        // the animation envelope keeps a 25% musical level visually near 25%
        // instead of letting the firmware's gamma turn it into roughly 6%.
        return normalized <= 0.0 ? 0.0 : Math.Sqrt(normalized);
    }

    private static RgbColor Gradient(MusicFrameOptions options, double position) =>
        position <= 0.5
            ? FrameColorMath.Lerp(options.LowColor, options.MidColor, position * 2.0)
            : FrameColorMath.Lerp(options.MidColor, options.HighColor, (position - 0.5) * 2.0);

    private static double Gated(double value, double noiseGate)
    {
        var normalized = FrameColorMath.Clamp01(value);
        var gate = Math.Clamp(noiseGate, 0.0, 1.0);
        return normalized <= gate ? 0.0 : normalized;
    }
}
