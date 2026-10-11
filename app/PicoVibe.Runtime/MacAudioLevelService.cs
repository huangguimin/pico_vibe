using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PicoVibe.App.Services;

public sealed class MacAudioLevelService : IAudioLevelService
{
    // A bridge that dies twice used to disable audio for the rest of the
    // session, and nothing but toggling Music mode off and on could revive it.
    // Replugging the card is enough to spend that budget: the device I/O churn
    // takes the bridge down with it.  Transient failures now back off instead
    // of latching, and a run that actually streamed audio clears the budget.
    private static readonly TimeSpan HealthyRunThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(2);
    // The bridge emits roughly 23 lines a second even when the system is
    // silent, so a gap this long means the capture session died while the
    // process stayed alive - which is what a sleep/wake cycle does to it.
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(5);
    // Waiting for a display to come back is not a failure, so it neither
    // spends the retry budget nor backs off: the lights should return within
    // seconds of the lid opening.
    private static readonly TimeSpan DisplayWaitDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly object stateGate = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private CancellationTokenSource? lifetime;
    private Task? supervisor;
    private Process? bridge;
    private AudioLevelSample current = AudioLevelSample.Unavailable(
        "System audio capture has not started.", "macOS Core Audio Tap");
    private volatile bool sawSampleThisRun;
    private double smoothedPeak;
    private double smoothedRms;
    private long lastSample;
    private bool retrySuppressed;
    private bool permissionDenied;
    private int disposed;

    static MacAudioLevelService()
    {
        // Last line of defence.  StopAsync/DisposeAsync cover the orderly
        // paths, but a force quit or a crash skips them and leaves the bridge
        // holding the audio tap, which silently starves the next launch.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAllBridges();
    }

    public event EventHandler<AudioLevelSample>? LevelChanged;
    public AudioLevelSample Current { get { lock (stateGate) return current; } }
    public bool IsRunning
    {
        get
        {
            lock (stateGate)
                return supervisor is { IsCompleted: false } || retrySuppressed;
        }
    }
    /// <summary>Shortened by tests; the defaults suit the real bridge.</summary>
    internal TimeSpan SampleStallTimeout { get; init; } = DefaultStallTimeout;

    internal TimeSpan DisplayRetryDelay { get; init; } = DisplayWaitDelay;
    internal TimeSpan TransientRetryDelay { get; init; } = MinRetryDelay;

    public TimeSpan AttackTime { get; set; } = TimeSpan.FromMilliseconds(30);
    public TimeSpan ReleaseTime { get; set; } = TimeSpan.FromMilliseconds(250);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (retrySuppressed || permissionDenied)
                {
                    return;
                }
                if (supervisor is { IsCompleted: false })
                {
                    return;
                }
                smoothedPeak = 0;
                smoothedRms = 0;
                lastSample = 0;
                lifetime = new CancellationTokenSource();
                supervisor = Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? task;
            CancellationTokenSource? source;
            lock (stateGate)
            {
                task = supervisor;
                source = lifetime;
                supervisor = null;
                lifetime = null;
                retrySuppressed = false;
            }
            source?.Cancel();
            StopBridge();
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            source?.Dispose();
            bool preservePermissionFailure;
            lock (stateGate) preservePermissionFailure = permissionDenied;
            if (!preservePermissionFailure)
            {
                Publish(AudioLevelSample.Unavailable("Audio monitoring is stopped.",
                    "macOS Core Audio Tap"));
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int transientFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? executable = ResolveBridge();
            if (executable is null)
            {
                SuppressRetries(
                    "PicoVibeMacBridge is missing from the application bundle.");
                return;
            }

            Process? process = null;
            bool terminalFailure = false;
            bool waitingForDisplay = false;
            string? lastError = null;
            sawSampleThisRun = false;
            long runStarted = Stopwatch.GetTimestamp();
            // Clear the field before claiming the tap, not after: only one
            // bridge may hold it, and a leftover makes ours return silence
            // while sitting at idle CPU with nothing on stdout.  Doing it here
            // also covers the runs that never get a bridge started at all.
            KillAllBridges();
            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo(executable, "audio")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                    EnableRaisingEvents = true,
                };
                process.Start();
                lock (stateGate) bridge = process;
                Task stderr = ConsumeErrorsAsync(process.StandardError, cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    Task<string?> read = process.StandardOutput
                        .ReadLineAsync(cancellationToken)
                        .AsTask();
                    string? line;
                    try
                    {
                        line = await read.WaitAsync(SampleStallTimeout, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Closing the lid stops the capture session without
                        // stopping the bridge, so nothing here ever ends and
                        // the levels simply freeze. Restart it.
                        _ = read.ContinueWith(
                            static abandoned => _ = abandoned.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        AppLog.Info(
                            "macOS audio bridge stopped delivering samples; restarting it.");
                        lastError = "System audio capture stalled and was restarted.";
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch (InvalidOperationException)
                        {
                            // It exited between the timeout and the kill.
                        }
                        break;
                    }

                    if (line is null)
                    {
                        break;
                    }
                    var outcome = ProcessLine(line);
                    terminalFailure |= outcome.Terminal;
                    if (!string.IsNullOrWhiteSpace(outcome.Error))
                    {
                        lastError = outcome.Error;
                    }
                }
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await stderr.ConfigureAwait(false);

                if (!terminalFailure && process.ExitCode != 0)
                {
                    // A permission denial must still latch: relaunching the
                    // bridge re-triggers the Screen Recording dialog every
                    // time, which is worse than staying quiet.  Anything else
                    // is transient and gets retried.
                    if (WasHealthyRun(runStarted))
                    {
                        transientFailures = 0;
                    }
                    if (IsDisplayUnavailable(lastError))
                    {
                        // Closing the lid takes the display away, so capture
                        // cannot start until it is back. Keep waiting instead
                        // of spending the failure budget on a shut lid.
                        waitingForDisplay = true;
                    }
                    else
                    {
                        transientFailures = Math.Min(transientFailures + 1, 5);
                        terminalFailure = IsPermissionDenial(lastError);
                    }
                    lastError ??=
                        $"PicoVibeMacBridge exited with code {process.ExitCode}.";
                }
                else if (process.ExitCode == 0 || WasHealthyRun(runStarted))
                {
                    transientFailures = 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Error("macOS audio bridge supervisor failed.", exception);
                lastError = $"System audio capture failed: {exception.Message}";
                Publish(AudioLevelSample.Unavailable(lastError, "macOS Core Audio Tap"));
                if (WasHealthyRun(runStarted))
                {
                    transientFailures = 0;
                }
                transientFailures = Math.Min(transientFailures + 1, 5);
                terminalFailure = IsPermissionDenial(lastError);
            }
            finally
            {
                lock (stateGate)
                {
                    if (ReferenceEquals(bridge, process)) bridge = null;
                }
                try
                {
                    if (process is { HasExited: false })
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    AppLog.Error("Stopping a stale macOS audio bridge failed.", exception);
                }
                process?.Dispose();
            }

            if (terminalFailure || IsPermissionDenial(lastError))
            {
                SuppressRetries(lastError ??
                    "Screen Recording permission is required for Music mode.");
                return;
            }

            // Back off instead of hammering a bridge that keeps dying, but keep
            // retrying: the usual cause is a transient device or system hiccup
            // that clears on its own.
            var delay = waitingForDisplay
                ? DisplayRetryDelay
                : TimeSpan.FromTicks(Math.Min(
                    TransientRetryDelay.Ticks << Math.Min(transientFailures, 4),
                    MaxRetryDelay.Ticks));
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool WasHealthyRun(long startedTimestamp) =>
        sawSampleThisRun &&
        Stopwatch.GetElapsedTime(startedTimestamp) >= HealthyRunThreshold;

    /// <summary>
    /// The bridge cannot capture while no display is awake. That clears by
    /// itself when the lid opens, so it must never latch the session off.
    /// </summary>
    internal static bool IsDisplayUnavailable(string? message) =>
        message?.Contains("No active macOS display", StringComparison.OrdinalIgnoreCase) == true;

    private void SuppressRetries(string message)
    {
        lock (stateGate)
        {
            retrySuppressed = true;
            permissionDenied |= IsPermissionDenial(message);
        }
        Publish(AudioLevelSample.Unavailable(message, "macOS Core Audio Tap"));
        AppLog.Info(
            "macOS audio capture will stay disabled for this app session: " +
            message);
    }

    private (bool Terminal, string? Error) ProcessLine(string line)
    {
        try
        {
            var message = JsonSerializer.Deserialize<BridgeMessage>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (message is null)
            {
                return (false, null);
            }
            if (!string.IsNullOrWhiteSpace(message.Error))
            {
                Publish(AudioLevelSample.Unavailable(message.Error, "macOS Core Audio Tap"));
                bool terminal = message.Terminal || IsPermissionDenial(message.Error);
                return (terminal, message.Error);
            }

            long now = Stopwatch.GetTimestamp();
            double seconds = lastSample == 0
                ? 0.04
                : Math.Clamp(Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds, 0.001, 1.0);
            lastSample = now;
            smoothedPeak = Smooth(smoothedPeak, Math.Clamp(message.Peak, 0, 1), seconds);
            smoothedRms = Smooth(smoothedRms, Math.Clamp(message.Rms, 0, 1), seconds);
            sawSampleThisRun = true;
            Publish(AudioLevelSample.Success(smoothedPeak, smoothedRms,
                message.Source ?? "macOS system audio"));
            return (false, null);
        }
        catch (JsonException)
        {
        }
        return (false, null);
    }

    private static bool IsPermissionDenial(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Screen Recording", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TCC", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("denied", StringComparison.OrdinalIgnoreCase);
    }

    private double Smooth(double value, double target, double seconds)
    {
        double constant = (target > value ? AttackTime : ReleaseTime).TotalSeconds;
        constant = Math.Max(constant, 0.001);
        return value + ((target - value) * (1.0 - Math.Exp(-seconds / constant)));
    }

    private async Task ConsumeErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;
            if (!string.IsNullOrWhiteSpace(line)) AppLog.Info($"macOS audio bridge: {line}");
        }
    }

    private void Publish(AudioLevelSample sample)
    {
        lock (stateGate) current = sample;
        try { LevelChanged?.Invoke(this, sample); }
        catch { }
    }

    private static string? ResolveBridge()
    {
        string? configured = Environment.GetEnvironmentVariable("PICO_VIBE_MAC_BRIDGE");
        string[] candidates =
        {
            configured ?? string.Empty,
            Path.Combine(AppContext.BaseDirectory, "PicoVibeMacBridge"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helpers", "PicoVibeMacBridge")),
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    /// <summary>
    /// Kills every PicoVibeMacBridge on the machine.  Bridges outlive a force
    /// quit or a crash, and only one may hold the ScreenCaptureKit tap, so the
    /// app reclaims the field itself instead of trusting every exit path.
    /// </summary>
    /// <remarks>
    /// Process.GetProcessesByName cannot be used: macOS caps a process name at
    /// 16 bytes (MAXCOMLEN) and "PicoVibeMacBridge" is 17, so the lookup
    /// silently matches nothing.  A bare `pkill -f` is not safe either -- it
    /// matches any command line merely containing the pattern, which during
    /// development killed the shell that had typed it.  So: find candidates by
    /// command line, then confirm each one really is the bridge before killing.
    /// </remarks>
    private static void KillAllBridges()
    {
        int reaped = 0;
        foreach (int pid in FindBridgePids())
        {
            if (pid == Environment.ProcessId || !IsBridgeProcess(pid))
            {
                continue;
            }
            try
            {
                using var victim = Process.GetProcessById(pid);
                victim.Kill(entireProcessTree: true);
                reaped++;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or
                    NotSupportedException or SystemException)
            {
                // Already gone, or not ours to signal.
            }
        }

        if (reaped > 0)
        {
            AppLog.Info($"Reclaimed the system-audio tap from {reaped} leftover bridge(s).");
        }
    }

    private static IReadOnlyList<int> FindBridgePids()
    {
        string output = RunTool("/usr/bin/pgrep", "-f", "PicoVibeMacBridge audio");
        var pids = new List<int>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line.Trim(), out int pid))
            {
                pids.Add(pid);
            }
        }
        return pids;
    }

    /// <summary>Confirms the pid's own executable is the bridge, not merely a
    /// process whose arguments mention it.</summary>
    private static bool IsBridgeProcess(int pid) =>
        RunTool("/bin/ps", "-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "comm=")
            .Trim()
            .EndsWith("PicoVibeMacBridge", StringComparison.Ordinal);

    private static string RunTool(string fileName, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start);
            if (process is null)
            {
                return string.Empty;
            }
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(); } catch { }
                return string.Empty;
            }
            return output;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private void StopBridge()
    {
        Process? process;
        lock (stateGate) process = bridge;
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        lifecycleGate.Dispose();
    }

    private sealed record BridgeMessage(
        double Peak,
        double Rms,
        string? Source,
        string? Error,
        bool Terminal = false);
}
