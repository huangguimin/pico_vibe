using System.Diagnostics;
using PicoVibe.App.Services;

namespace PicoVibe.Runtime.Tests;

public sealed class SleepGuardTests
{
    [Fact]
    public void GuardTakesAndReleasesTheRealPowerAssertion()
    {
        using var guard = new SleepGuard();
        Assert.False(guard.Held);

        guard.Apply(true);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            Assert.True(guard.Held);
        }
        else
        {
            // No power assertion API here; the guard must report honestly
            // instead of pretending the machine will stay awake.
            Assert.False(guard.Held);
        }

        guard.Apply(false);
        Assert.False(guard.Held);
    }

    /// <summary>
    /// macOS silently drops a non-ASCII assertion name, and an unnamed
    /// assertion tells nobody who is holding the machine awake.
    /// </summary>
    [Fact]
    public void MacAssertionIsListedWithAReadableName()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var guard = new SleepGuard();
        guard.Apply(true);
        try
        {
            Assert.True(guard.Held);
            var line = ReadPowerAssertions()
                .Split('\n')
                .FirstOrDefault(text => text.Contains(
                    $"pid {Environment.ProcessId}(",
                    StringComparison.Ordinal));

            Assert.NotNull(line);
            Assert.DoesNotContain("named: \"\"", line, StringComparison.Ordinal);
            Assert.Contains("Pico Vibe", line, StringComparison.Ordinal);
        }
        finally
        {
            guard.Apply(false);
        }
    }

    private static string ReadPowerAssertions()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/pmset", "-g assertions")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public void RepeatedApplyCallsAreIdempotent()
    {
        using var guard = new SleepGuard();

        guard.Apply(true);
        var afterFirst = guard.Held;
        guard.Apply(true);
        Assert.Equal(afterFirst, guard.Held);

        guard.Apply(false);
        guard.Apply(false);
        Assert.False(guard.Held);
    }
}
