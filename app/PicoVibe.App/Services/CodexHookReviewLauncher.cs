using System.Diagnostics;
using System.IO;

namespace PicoVibe.App.Services;

public static class CodexHookReviewLauncher
{
    public static async Task InstallHooksAndOpenAsync(
        string applicationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var hookExecutable = Path.Combine(applicationDirectory, "PicoVibeHook.exe");
        if (!File.Exists(hookExecutable))
        {
            throw new FileNotFoundException(
                "未找到 PicoVibeHook.exe，请重新运行 Pico Vibe 安装包。",
                hookExecutable);
        }

        using var installer = Process.Start(new ProcessStartInfo
        {
            FileName = hookExecutable,
            ArgumentList = { "install-hooks" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("无法启动 Pico Vibe Hook 安装程序。");

        var outputTask = installer.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = installer.StandardError.ReadToEndAsync(cancellationToken);
        await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (installer.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"重新安装 Hook 失败（退出码 {installer.ExitCode}）：{detail.Trim()}");
        }

        var codexCommand = FindCodexCommand(
            Environment.GetEnvironmentVariable("PATH"));
        if (codexCommand is null)
        {
            throw new FileNotFoundException(
                "未找到 Codex CLI。请先安装 Codex，然后在 Codex 中输入 /hooks。",
                "codex.cmd");
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = codexCommand,
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        }) ?? throw new InvalidOperationException("无法打开 Codex 终端。");
    }

    public static string? FindCodexCommand(string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (var fileName in new[] { "codex.cmd", "codex.exe" })
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
