namespace PicoVibe.Core.Device;

/// <summary>
/// Identifies the fixed-mailbox firmware used by the customer application.
/// A volume label alone is not an identity check: ordinary removable media can
/// have the same label and must never be opened for Pico Vibe writes.
/// </summary>
public static class PicoDeviceCompatibility
{
    public static bool IsSupported(PicoDeviceLocation device)
    {
        ArgumentNullException.ThrowIfNull(device);
        try
        {
            return Directory.Exists(device.RootPath) &&
                File.Exists(device.TouchPath) &&
                File.Exists(device.VibeSlot0Path) &&
                new FileInfo(device.VibeSlot0Path).Length == VibePacketEncoder.SectorSize &&
                File.Exists(device.VibeSlot1Path) &&
                new FileInfo(device.VibeSlot1Path).Length == VibePacketEncoder.SectorSize;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Confirms a previously resolved device path is still reachable. On macOS an
    /// abrupt USB unplug can leave a stale /Volumes entry behind while
    /// Directory.Exists still succeeds briefly; a real open/read catches that sooner.
    /// </summary>
    public static bool IsAccessible(PicoDeviceLocation device)
    {
        ArgumentNullException.ThrowIfNull(device);
        try
        {
            if (!Directory.Exists(device.RootPath))
            {
                return false;
            }

            return ProbeMailbox(device.VibeSlot0Path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ProbeMailbox(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1,
                FileOptions.None);
            Span<byte> scratch = stackalloc byte[1];
            return stream.Read(scratch) == 1;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
