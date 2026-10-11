namespace PicoVibe.Core.Lighting;

/// <summary>An RGB888 color. Components are clamped when rendered.</summary>
public readonly record struct RgbColor(int R, int G, int B)
{
    public static RgbColor Black { get; } = new(0, 0, 0);
    public static RgbColor White { get; } = new(255, 255, 255);
}
