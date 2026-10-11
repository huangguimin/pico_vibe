using System.Diagnostics;

namespace PicoVibe.App.Services;

public sealed class MacSystemMediaController : ISystemMediaController
{
    internal enum BridgeResult
    {
        Success,
        NoActiveSession,
        Unavailable,
        Rejected,
        Failed,
    }

    private const string Script = """
        if application "Music" is running then
          tell application "Music" to next track
          return "Music"
        end if
        if application "Spotify" is running then
          tell application "Spotify" to next track
          return "Spotify"
        end if
        error "No supported player is running"
        """;

    public bool SendNextTrack()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            BridgeResult bridgeResult = TrySendSystemMediaCommand();
            if (bridgeResult == BridgeResult.Success)
            {
                return true;
            }
            if (bridgeResult != BridgeResult.Unavailable)
            {
                return false;
            }

            // Only use Apple Events when the bundled low-permission bridge is
            // genuinely unavailable. A missing Now Playing session is not a
            // reason to prompt for Automation access or guess which open
            // player the user meant.
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("/usr/bin/osascript")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            process.StandardInput.Write(Script);
            process.StandardInput.Close();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            bool accepted = process.ExitCode == 0;
            if (!accepted)
            {
                string error = process.StandardError.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(error))
                    AppLog.Info($"macOS player automation did not accept next-track: {error}");
            }
            return accepted;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            AppLog.Error("macOS next-track automation failed.", exception);
            return false;
        }
    }

    private static BridgeResult TrySendSystemMediaCommand()
    {
        string? bridge = ResolveBridge();
        if (bridge is null) return BridgeResult.Unavailable;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(bridge, "next-track")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return BridgeResult.Failed;
            }
            BridgeResult result = ClassifyBridgeExitCode(process.ExitCode);
            if (result == BridgeResult.Success) return result;

            string error = process.StandardError.ReadToEnd().Trim();
            string output = process.StandardOutput.ReadToEnd().Trim();
            string detail = string.IsNullOrEmpty(error) ? output : error;
            if (!string.IsNullOrEmpty(detail))
                AppLog.Info($"macOS system media command was rejected: {detail}");
            return result;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            AppLog.Error("macOS system media command failed.", exception);
        }
        return BridgeResult.Failed;
    }

    internal static BridgeResult ClassifyBridgeExitCode(int exitCode) => exitCode switch
    {
        0 => BridgeResult.Success,
        2 => BridgeResult.NoActiveSession,
        3 => BridgeResult.Unavailable,
        4 => BridgeResult.Rejected,
        _ => BridgeResult.Failed,
    };

    private static string? ResolveBridge()
    {
        string? configured = Environment.GetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE");
        string[] candidates =
        {
            configured ?? string.Empty,
            Path.Combine(AppContext.BaseDirectory, "PicoVibeMacBridge"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helpers", "PicoVibeMacBridge")),
        };
        return candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}
