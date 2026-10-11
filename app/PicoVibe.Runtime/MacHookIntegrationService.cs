using System.Diagnostics;

namespace PicoVibe.App.Services;

public sealed class MacHookIntegrationService
{
    public Task<(bool Success, string Message)> InstallAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync("install-hooks", includeSkill: true, cancellationToken);

    public Task<(bool Success, string Message)> UninstallAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync("uninstall-hooks", includeSkill: false, cancellationToken);

    private static async Task<(bool Success, string Message)> RunAsync(
        string command,
        bool includeSkill,
        CancellationToken cancellationToken)
    {
        string? helper = ResolveHelper();
        if (helper is null)
            return (false, "应用包中缺少 PicoVibeHook。请重新安装完整的 Pico Vibe.app。");

        var arguments = new List<string> { command };
        if (includeSkill)
        {
            string? skill = ResolveSkill();
            if (skill is null)
                return (false, "应用包中缺少 Codex Skill 资源。请重新安装完整安装包。");
            arguments.Add("--skill-source");
            arguments.Add(skill);
        }

        var start = new ProcessStartInfo(helper)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return (false, "无法启动 PicoVibeHook。");
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = (await outputTask.ConfigureAwait(false)).Trim();
            string error = (await errorTask.ConfigureAwait(false)).Trim();
            string message = string.IsNullOrWhiteSpace(output) ? error : output;
            return (process.ExitCode == 0, string.IsNullOrWhiteSpace(message)
                ? (process.ExitCode == 0 ? "操作完成。" : "操作失败。")
                : message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, exception.Message);
        }
    }

    private static string? ResolveHelper() => ResolveFirst(
        Environment.GetEnvironmentVariable("PICO_VIBE_HOOK"),
        Path.Combine(AppContext.BaseDirectory, "PicoVibeHook"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helpers", "PicoVibeHook")));

    private static string? ResolveSkill() => ResolveDirectory(
        Environment.GetEnvironmentVariable("PICO_VIBE_SKILL_SOURCE"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "integrations",
            "codex", "pico-vibe-status")));

    private static string? ResolveFirst(params string?[] candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? ResolveDirectory(params string?[] candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
}
