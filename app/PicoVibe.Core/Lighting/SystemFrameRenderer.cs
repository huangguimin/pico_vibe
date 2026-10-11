using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Lighting;

/// <summary>Renders a normalized system metric into one RGB888 frame.</summary>
public static class SystemFrameRenderer
{
    public static byte[] RenderValue(
        double value,
        double displayMinimum,
        double displayMaximum,
        SystemFrameOptions options,
        FrameRenderContext context) =>
        Render(NormalizeValue(value, displayMinimum, displayMaximum), options, context);

    public static double NormalizeValue(
        double value,
        double displayMinimum,
        double displayMaximum)
    {
        if (!double.IsFinite(value) ||
            !double.IsFinite(displayMinimum) ||
            !double.IsFinite(displayMaximum) ||
            displayMaximum <= displayMinimum)
        {
            return 0.0;
        }

        return FrameColorMath.Clamp01(
            (value - displayMinimum) / (displayMaximum - displayMinimum));
    }

    public static byte[] Render(
        double normalizedValue,
        SystemFrameOptions options,
        FrameRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        var physicalCount = Math.Clamp(context.LedCount, 1, 64);
        var start = Math.Clamp(context.SegmentStart, 0, physicalCount - 1);
        var count = Math.Clamp(context.SegmentCount, 1, physicalCount - start);
        var value = FrameColorMath.Clamp01(normalizedValue);
        var frame = new byte[physicalCount * 3];

        for (var index = 0; index < count; index++)
        {
            var position = count == 1 ? 1.0 : index / (double)(count - 1);
            var color = FrameColorMath.Lerp(options.LowColor, options.HighColor, position);
            var intensity = options.Visualizer switch
            {
                SystemVisualizer.Bar or SystemVisualizer.GradientBar =>
                    Math.Clamp((value * count) - index, 0.0, 1.0),
                SystemVisualizer.Pulse => value,
                SystemVisualizer.Comet => CometIntensity(index, count, value),
                SystemVisualizer.CenterBar => CenterBarIntensity(index, count, value),
                SystemVisualizer.SegmentedBar => SegmentedBarIntensity(index, count, value),
                SystemVisualizer.ColorWash => 1.0,
                SystemVisualizer.Breathing => BreathingIntensity(value, context.Phase),
                _ => 0.0,
            };
            if (options.Visualizer == SystemVisualizer.Bar)
            {
                color = options.LowColor;
            }
            else if (options.Visualizer is
                SystemVisualizer.Pulse or
                SystemVisualizer.Comet or
                SystemVisualizer.ColorWash or
                SystemVisualizer.Breathing)
            {
                color = FrameColorMath.Lerp(options.LowColor, options.HighColor, value);
            }
            else if (options.Visualizer == SystemVisualizer.CenterBar)
            {
                color = FrameColorMath.Lerp(options.LowColor, options.HighColor, value);
            }
            else if (options.Visualizer == SystemVisualizer.SegmentedBar)
            {
                color = FrameColorMath.Lerp(
                    options.LowColor,
                    options.HighColor,
                    SegmentedBarPosition(index, count));
            }

            var destination = start + (options.Reverse ? count - index - 1 : index);
            FrameColorMath.Write(
                frame,
                destination,
                physicalCount,
                false,
                color,
                options.BackgroundColor,
                intensity,
                context.GlobalBrightnessPercent);
        }

        return frame;
    }

    private static double CometIntensity(int index, int ledCount, double value)
    {
        var head = value * Math.Max(0, ledCount - 1);
        var distance = Math.Abs(index - head);
        return Math.Max(0.0, 1.0 - (distance / 3.0));
    }

    private static double CenterBarIntensity(int index, int ledCount, double value)
    {
        var center = (ledCount - 1) / 2.0;
        var layer = Math.Floor(Math.Abs(index - center));
        var layerCount = Math.Ceiling(ledCount / 2.0);
        return Math.Clamp((value * layerCount) - layer, 0.0, 1.0);
    }

    private static double SegmentedBarIntensity(int index, int ledCount, double value)
    {
        var blockCount = Math.Min(6, ledCount);
        var block = Math.Min(blockCount - 1, index * blockCount / ledCount);
        return Math.Clamp((value * blockCount) - block, 0.0, 1.0);
    }

    private static double SegmentedBarPosition(int index, int ledCount)
    {
        var blockCount = Math.Min(6, ledCount);
        var block = Math.Min(blockCount - 1, index * blockCount / ledCount);
        return blockCount == 1 ? 0.0 : block / (double)(blockCount - 1);
    }

    private static double BreathingIntensity(double value, double phase)
    {
        var normalizedPhase = double.IsFinite(phase) ? phase - Math.Floor(phase) : 0.0;
        var wave = 0.5 - (0.5 * Math.Cos(normalizedPhase * Math.PI * 2.0));
        var metricBrightness = 0.15 + (0.85 * value);
        return metricBrightness * (0.25 + (0.75 * wave));
    }
}
