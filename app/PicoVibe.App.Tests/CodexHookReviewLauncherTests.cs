using PicoVibe.App.Services;

namespace PicoVibe.App.Tests;

public sealed class CodexHookReviewLauncherTests
{
    [Fact]
    public void FindCodexCommandSupportsQuotedPathWithSpaces()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PicoVibeCodexTests",
            Guid.NewGuid().ToString("N"),
            "Program Files");
        Directory.CreateDirectory(root);
        var command = Path.Combine(root, "codex.cmd");
        File.WriteAllText(command, "@exit /b 0");
        try
        {
            Assert.Equal(command, CodexHookReviewLauncher.FindCodexCommand($"\"{root}\""));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public void FindCodexCommandSkipsRelativeAndMissingEntries()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PicoVibeCodexTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var command = Path.Combine(root, "codex.exe");
        File.WriteAllBytes(command, Array.Empty<byte>());
        try
        {
            var path = $"relative{Path.PathSeparator}{Path.Combine(root, "missing")}{Path.PathSeparator}{root}";
            Assert.Equal(command, CodexHookReviewLauncher.FindCodexCommand(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }
}
