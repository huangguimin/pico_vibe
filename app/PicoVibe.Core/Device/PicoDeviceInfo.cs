using System.Globalization;

namespace PicoVibe.Core.Device;

/// <summary>
/// What the device says about itself, read from INFO.TXT at connect time.
/// The firmware writes plain key=value lines; unknown keys are ignored so a
/// newer board can add fields without breaking an older application, and a
/// missing file means an older firmware rather than an incompatible device.
/// </summary>
public sealed record PicoDeviceInfo(
    string FirmwareVersion,
    string BoardName,
    int BoardId,
    string Protocol,
    int LedCount)
{
    /// <summary>
    /// Firmware from before INFO.TXT existed. It is the original board with
    /// twelve lamps, which is the only hardware that can be running it.
    /// </summary>
    public static PicoDeviceInfo Legacy { get; } =
        new("unknown", "production_v1", 1, "vibe1", 12);

    public bool IsLegacy => ReferenceEquals(this, Legacy);

    public static PicoDeviceInfo Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Legacy;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            fields[line[..separator].Trim()] = line[(separator + 1)..].Trim('\r', ' ');
        }

        // Without the format marker this is not a device info file at all.
        if (!fields.ContainsKey("pico_vibe"))
        {
            return Legacy;
        }

        return new PicoDeviceInfo(
            Text(fields, "fw", "unknown"),
            Text(fields, "board", Legacy.BoardName),
            Number(fields, "board_id", Legacy.BoardId),
            Text(fields, "proto", Legacy.Protocol),
            Number(fields, "leds", Legacy.LedCount));
    }

    public string Describe() =>
        $"固件 {FirmwareVersion}，硬件 {BoardName}(v{BoardId})，{LedCount} 灯，协议 {Protocol}";

    private static string Text(IReadOnlyDictionary<string, string> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static int Number(IReadOnlyDictionary<string, string> fields, string key, int fallback) =>
        fields.TryGetValue(key, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
        parsed > 0
            ? parsed
            : fallback;
}
