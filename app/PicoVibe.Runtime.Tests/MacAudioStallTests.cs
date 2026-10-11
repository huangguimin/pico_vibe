using PicoVibe.App.Services;

namespace PicoVibe.Runtime.Tests;

public sealed class MacAudioStallTests
{
    /// <summary>
    /// Closing the lid stops the capture session without stopping the bridge:
    /// stdout simply goes quiet, with no exception and no exit code, so the
    /// levels freeze until the user toggles the mode. The supervisor has to
    /// notice the silence itself.
    /// </summary>
    [Fact]
    public async Task StalledBridgeIsRestarted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("pico-vibe-audio-stall");
        var script = Path.Combine(directory.FullName, "fake-bridge.sh");
        // One sample, then the process lives on saying nothing at all.
        await File.WriteAllTextAsync(
            script,
            "#!/bin/bash\n" +
            "echo '{\"peak\":0.5,\"rms\":0.25,\"source\":\"fake\"}'\n" +
            "sleep 120\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var previous = Environment.GetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE");
        Environment.SetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE", script);
        var service = new MacAudioLevelService
        {
            SampleStallTimeout = TimeSpan.FromMilliseconds(500),
        };
        var samples = 0;
        service.LevelChanged += (_, sample) =>
        {
            if (sample.Available)
            {
                Interlocked.Increment(ref samples);
            }
        };

        try
        {
            await service.StartAsync();

            // A second sample can only arrive from a second bridge run.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref samples) < 2)
            {
                await Task.Delay(100);
            }

            Assert.True(
                Volatile.Read(ref samples) >= 2,
                $"The stalled bridge was not restarted; {Volatile.Read(ref samples)} sample(s) arrived.");
        }
        finally
        {
            await service.DisposeAsync();
            Environment.SetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE", previous);
            try { directory.Delete(recursive: true); } catch (IOException) { }
        }
    }
    /// <summary>
    /// A shut lid takes the display away and ScreenCaptureKit refuses to start.
    /// That used to be reported as terminal, so the first restart after closing
    /// the lid disabled audio for the rest of the session and opening the lid
    /// again never brought it back.
    /// </summary>
    [Theory]
    [InlineData("No active macOS display is available for system audio capture.")]
    [InlineData("Start system audio capture failed (Core Audio OSStatus -1).")]
    public async Task TransientFailureKeepsRetryingInsteadOfLatchingOff(string error)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("pico-vibe-audio-display");
        var script = Path.Combine(directory.FullName, "fake-bridge.sh");
        var attempts = Path.Combine(directory.FullName, "attempts.txt");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/bash\n" +
            $"echo run >> '{attempts}'\n" +
            "echo '{\"peak\":0,\"rms\":0," +
            $"\"error\":\"{error}\"," +
            "\"terminal\":false}'\n" +
            "exit 1\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var previous = Environment.GetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE");
        Environment.SetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE", script);
        var service = new MacAudioLevelService
        {
            SampleStallTimeout = TimeSpan.FromSeconds(30),
            DisplayRetryDelay = TimeSpan.FromMilliseconds(200),
            TransientRetryDelay = TimeSpan.FromMilliseconds(10),
        };

        try
        {
            await service.StartAsync();

            // The transient budget is four runs, so reaching six proves the
            // service is still waiting for the display rather than latched off.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            var runs = 0;
            while (DateTimeOffset.UtcNow < deadline && runs < 6)
            {
                await Task.Delay(100);
                runs = File.Exists(attempts) ? File.ReadAllLines(attempts).Length : 0;
            }

            Assert.True(runs >= 6, $"The bridge stopped being retried after {runs} run(s).");
        }
        finally
        {
            await service.DisposeAsync();
            Environment.SetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE", previous);
            try { directory.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DisplayUnavailableIsRecognised()
    {
        Assert.True(MacAudioLevelService.IsDisplayUnavailable(
            "No active macOS display is available for system audio capture."));
        Assert.False(MacAudioLevelService.IsDisplayUnavailable(
            "Screen Recording permission was denied."));
        Assert.False(MacAudioLevelService.IsDisplayUnavailable(null));
    }

    [Fact]
    public async Task AudioPermissionDenialDoesNotRestartIntoPromptLoop()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var directory = Directory.CreateTempSubdirectory("pico-vibe-audio-denial");
        var script = Path.Combine(directory.FullName, "fake-bridge.sh");
        var attempts = Path.Combine(directory.FullName, "attempts.txt");
        await File.WriteAllTextAsync(script,
            "#!/bin/bash\n" +
            $"echo run >> '{attempts}'\n" +
            "echo '{\"error\":\"System audio capture permission denied\",\"terminal\":true}'\n" +
            "exit 1\n");
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var previous = Environment.GetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE");
        Environment.SetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE", script);
        var service = new MacAudioLevelService { TransientRetryDelay = TimeSpan.FromMilliseconds(10) };
        try
        {
            await service.StartAsync();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!service.Current.Reason!.Contains("permission", StringComparison.OrdinalIgnoreCase)
                   && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(50);
            await Task.Delay(500);
            Assert.Single(File.ReadAllLines(attempts));
            Assert.False(service.Current.Available);
        }
        finally
        {
            await service.DisposeAsync();
            Environment.SetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE", previous);
            directory.Delete(recursive: true);
        }
    }
}
