namespace PicoVibe.Core.Lighting;

internal static class FrameColorMath
{
    public static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    public static RgbColor Clamp(RgbColor color) => new(
        Math.Clamp(color.R, 0, 255),
        Math.Clamp(color.G, 0, 255),
        Math.Clamp(color.B, 0, 255));

    public static RgbColor Lerp(RgbColor from, RgbColor to, double amount)
    {
        var t = Clamp01(amount);
        return new RgbColor(
            (int)Math.Round(from.R + ((to.R - from.R) * t)),
            (int)Math.Round(from.G + ((to.G - from.G) * t)),
            (int)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    public static RgbColor Hsv(double hue, double saturation, double value)
    {
        hue = double.IsFinite(hue) ? hue - Math.Floor(hue) : 0;
        saturation = Clamp01(saturation);
        value = Clamp01(value);
        var scaled = hue * 6.0;
        var sector = (int)Math.Floor(scaled) % 6;
        var fraction = scaled - Math.Floor(scaled);
        var p = value * (1.0 - saturation);
        var q = value * (1.0 - (fraction * saturation));
        var t = value * (1.0 - ((1.0 - fraction) * saturation));
        var (r, g, b) = sector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return new RgbColor(
            (int)Math.Round(r * 255),
            (int)Math.Round(g * 255),
            (int)Math.Round(b * 255));
    }

    public static void Write(
        byte[] frame,
        int logicalIndex,
        int ledCount,
        bool reverse,
        RgbColor foreground,
        RgbColor background,
        double intensity,
        int globalBrightnessPercent)
    {
        var physical = reverse ? ledCount - logicalIndex - 1 : logicalIndex;
        var mixed = Lerp(background, foreground, Clamp01(intensity));
        var scale = Math.Clamp(globalBrightnessPercent, 0, 100) / 100.0;
        var offset = physical * 3;
        frame[offset] = ToByte(mixed.R * scale);
        frame[offset + 1] = ToByte(mixed.G * scale);
        frame[offset + 2] = ToByte(mixed.B * scale);
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
