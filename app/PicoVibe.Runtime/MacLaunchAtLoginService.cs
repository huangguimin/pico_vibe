using System.Diagnostics;

namespace PicoVibe.App.Services;

public sealed class MacLaunchAtLoginService
{
    public async Task<bool> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        string? bridge = ResolveBridge();
        if (bridge is null) return false;
        using var process = Process.Start(new ProcessStartInfo(
            bridge,
            enabled ? "login-register" : "login-unregister")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) return false;
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    private static string? ResolveBridge()
    {
        string[] candidates =
        {
            Environment.GetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE") ?? string.Empty,
            Path.Combine(AppContext.BaseDirectory, "PicoVibeMacBridge"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helpers", "PicoVibeMacBridge")),
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}
