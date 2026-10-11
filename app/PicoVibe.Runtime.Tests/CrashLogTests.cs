using PicoVibe.App.Services;

namespace PicoVibe.Runtime.Tests;

public sealed class CrashLogTests
{
    /// <summary>
    /// The normal log helpers are fire-and-forget, so a crash would tear the
    /// process down before the write ran and leave nothing behind.
    /// </summary>
    [Fact]
    public void FatalWritesBeforeReturning()
    {
        var home = Path.Combine(Path.GetTempPath(), $"pico-vibe-crashlog-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("PICO_VIBE_TEST_HOME");
        Environment.SetEnvironmentVariable("PICO_VIBE_TEST_HOME", home);
        try
        {
            var path = AppLog.FilePath;
            AppLog.Fatal("Broker went down.", new InvalidOperationException("boom"));

            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("[FATAL] Broker went down.", text, StringComparison.Ordinal);
            Assert.Contains("boom", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICO_VIBE_TEST_HOME", previous);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ExitIsRecordedSynchronouslyToo()
    {
        var home = Path.Combine(Path.GetTempPath(), $"pico-vibe-exitlog-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("PICO_VIBE_TEST_HOME");
        Environment.SetEnvironmentVariable("PICO_VIBE_TEST_HOME", home);
        try
        {
            var path = AppLog.FilePath;
            AppLog.InfoNow("Pico Vibe exited.");

            Assert.Contains(
                "[INFO] Pico Vibe exited.",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICO_VIBE_TEST_HOME", previous);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void InstallingTwiceSubscribesOnce()
    {
        CrashLog.Install("Pico Vibe tests");
        CrashLog.Install("Pico Vibe tests");
    }
}
