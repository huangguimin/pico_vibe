namespace PicoVibe.Core.Device;

/// <summary>Resolved paths for one mounted PICO_RGB device.</summary>
public sealed record PicoDeviceLocation
{
    public PicoDeviceLocation(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }
    public string DeviceId => RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public string RgbIniPath => Path.Combine(RootPath, "RGB.INI");
    public string LedBinPath => Path.Combine(RootPath, "LED.BIN");
    public string VibeSlot0Path => Path.Combine(RootPath, "VIBE0.BIN");
    public string VibeSlot1Path => Path.Combine(RootPath, "VIBE1.BIN");
    public string TouchPath => Path.Combine(RootPath, "TOUCH.TXT");
    public string InfoPath => Path.Combine(RootPath, "INFO.TXT");
}
