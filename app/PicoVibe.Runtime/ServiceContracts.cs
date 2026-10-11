using PicoVibe.Core.Configuration;

namespace PicoVibe.App.Services;

public sealed record MetricSample(
    double? Value,
    bool Available,
    string Reason,
    string Source,
    DateTimeOffset Timestamp)
{
    public static MetricSample Success(double value, string source) =>
        new(value, true, string.Empty, source, DateTimeOffset.UtcNow);

    public static MetricSample Unavailable(string reason, string source) =>
        new(null, false, reason, source, DateTimeOffset.UtcNow);
}

public sealed record MetricReading(SystemMetric Metric, MetricSample Sample);

public sealed record AudioLevelSample(
    double Peak,
    double Rms,
    bool Available,
    string Reason,
    string Source,
    DateTimeOffset Timestamp)
{
    public static AudioLevelSample Success(double peak, double rms, string source) =>
        new(peak, rms, true, string.Empty, source, DateTimeOffset.UtcNow);

    public static AudioLevelSample Unavailable(string reason, string source = "Audio capture") =>
        new(0, 0, false, reason, source, DateTimeOffset.UtcNow);
}

public interface ISystemMetricService
{
    Task<MetricSample> GetSampleAsync(
        SystemMetric metric,
        CancellationToken cancellationToken = default);
}

public interface IAudioLevelService : IAsyncDisposable
{
    event EventHandler<AudioLevelSample> LevelChanged;
    AudioLevelSample Current { get; }
    bool IsRunning { get; }
    TimeSpan AttackTime { get; set; }
    TimeSpan ReleaseTime { get; set; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface ISystemMediaController
{
    bool SendNextTrack();
}

public sealed class UnavailableSystemMetricService : ISystemMetricService
{
    public Task<MetricSample> GetSampleAsync(
        SystemMetric metric,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(MetricSample.Unavailable(
            $"{metric} is unavailable on this platform.",
            "Pico Vibe"));
    }
}

public sealed class UnavailableAudioLevelService : IAudioLevelService
{
    public event EventHandler<AudioLevelSample>? LevelChanged;
    public AudioLevelSample Current { get; private set; } =
        AudioLevelSample.Unavailable("Audio capture is unavailable on this platform.");
    public bool IsRunning { get; private set; }
    public TimeSpan AttackTime { get; set; } = TimeSpan.FromMilliseconds(30);
    public TimeSpan ReleaseTime { get; set; } = TimeSpan.FromMilliseconds(250);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsRunning = true;
        LevelChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsRunning = false;
        return ValueTask.CompletedTask;
    }
}

public sealed class NullSystemMediaController : ISystemMediaController
{
    public bool SendNextTrack() => false;
}
