using System.Diagnostics;

namespace PicoVibe.Core.Ipc;

/// <summary>Local process evidence only; never an authorization capability.</summary>
public static class AgentProcessIdentity
{
    public static (int? Pid, long? Start) Capture(HookPlatform platform)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return (null, null);
        try
        {
            var pid = Environment.ProcessId;
            for (var depth = 0; depth < 10 && pid > 1; depth++)
            {
                using var ps = Process.Start(new ProcessStartInfo("/bin/ps")
                {
                    ArgumentList = { "-p", pid.ToString(), "-o", "ppid=", "-o", "comm=" },
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                if (ps is null) break;
                if (!ps.WaitForExit(500)) { ps.Kill(); break; }
                var line = ps.StandardOutput.ReadToEnd().Trim();
                var split = line.IndexOfAny(new[] { ' ', '\t' });
                if (split < 0 || !int.TryParse(line[..split], out var parent)) break;
                var path = line[(split + 1)..].Trim();
                if (depth > 0 && Matches(platform, path))
                {
                    using var owner = Process.GetProcessById(pid);
                    return (pid, owner.StartTime.ToUniversalTime().Ticks);
                }
                pid = parent;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or IOException) { }
        return (null, null);
    }

    private static bool Matches(HookPlatform platform, string path) => platform switch
    {
        HookPlatform.Cursor => path.Contains("/cursor-agent/", StringComparison.Ordinal) || Path.GetFileName(path) is "cursor-agent" or "cursor-agent-sea",
        HookPlatform.Codex => Path.GetFileName(path) == "codex",
        HookPlatform.Claude => Path.GetFileName(path) == "claude",
        _ => false,
    };

    public static bool? IsAlive(int pid, long startUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
