using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PicoVibe.Core.Configuration;

namespace PicoVibe.App.Services;

public sealed partial class MacSystemMetricService : ISystemMetricService
{
    private static readonly TimeSpan BatteryCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TemperatureCacheDuration = TimeSpan.FromSeconds(10);
    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const int CpuUser = 0;
    private const int CpuSystem = 1;
    private const int CpuIdle = 2;
    private const int CpuNice = 3;
    private readonly object cpuGate = new();
    private readonly object batteryGate = new();
    private readonly object temperatureGate = new();
    private readonly SemaphoreSlim batteryProbeLock = new(1, 1);
    private readonly SemaphoreSlim temperatureProbeLock = new(1, 1);
    private CpuTicks? previousCpu;
    private CacheEntry? cachedBattery;
    private TemperatureCacheEntry? cachedTemperatures;

    public Task<MetricSample> GetSampleAsync(
        SystemMetric metric,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return metric switch
        {
            SystemMetric.CpuUsage => Task.FromResult(GetCpuUsage()),
            SystemMetric.MemoryUsage => Task.FromResult(GetMemoryUsage()),
            SystemMetric.CpuTemperature or
                SystemMetric.GpuTemperature or
                SystemMetric.DiskTemperature => GetTemperatureAsync(metric, cancellationToken),
            SystemMetric.BatteryLevel => GetBatteryLevelAsync(cancellationToken),
            _ => Task.FromResult(MetricSample.Unavailable("Unsupported metric.", "macOS")),
        };
    }

    private MetricSample GetCpuUsage()
    {
        if (!OperatingSystem.IsMacOS() || !TryReadCpuTicks(out var current))
        {
            return MetricSample.Unavailable("Unable to read host CPU ticks.", "macOS Mach host_statistics");
        }

        lock (cpuGate)
        {
            var previous = previousCpu;
            previousCpu = current;
            if (previous is null)
            {
                return MetricSample.Unavailable("CPU utilization is warming up.", "macOS Mach host_statistics");
            }

            ulong busyDelta = Delta(current.User, previous.Value.User) +
                              Delta(current.System, previous.Value.System) +
                              Delta(current.Nice, previous.Value.Nice);
            ulong idleDelta = Delta(current.Idle, previous.Value.Idle);
            ulong total = busyDelta + idleDelta;
            return total == 0
                ? MetricSample.Unavailable("Not enough elapsed CPU time.", "macOS Mach host_statistics")
                : MetricSample.Success(Math.Clamp((double)busyDelta * 100.0 / total, 0.0, 100.0),
                    "macOS Mach host_statistics");
        }
    }

    private static MetricSample GetMemoryUsage()
    {
        if (!OperatingSystem.IsMacOS() ||
            !TryReadUInt64Sysctl("hw.memsize", out ulong totalBytes) ||
            totalBytes == 0)
        {
            return MetricSample.Unavailable("Unable to read physical memory size.", "macOS sysctl");
        }

        uint host = MachHostSelf();
        var stats = new uint[64];
        uint count = (uint)stats.Length;
        if (HostStatistics64(host, HostVmInfo64, stats, ref count) != 0 || count < 4 ||
            HostPageSize(host, out uint pageSize) != 0)
        {
            return MetricSample.Unavailable("Unable to read VM statistics.", "macOS Mach host_statistics64");
        }

        ulong availablePages = (ulong)stats[0] + stats[2]; // free + inactive
        ulong availableBytes = Math.Min(totalBytes, availablePages * pageSize);
        double percent = (double)(totalBytes - availableBytes) * 100.0 / totalBytes;
        return MetricSample.Success(Math.Clamp(percent, 0.0, 100.0),
            "macOS Mach host_statistics64");
    }

    private async Task<MetricSample> GetTemperatureAsync(
        SystemMetric metric,
        CancellationToken cancellationToken)
    {
        MacTemperatureSnapshot snapshot = await GetTemperatureSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshot.For(metric);
    }

    private async Task<MacTemperatureSnapshot> GetTemperatureSnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (TryGetCachedTemperatures(out MacTemperatureSnapshot cached))
        {
            return cached;
        }

        await temperatureProbeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedTemperatures(out cached))
            {
                return cached;
            }

            MacTemperatureSnapshot snapshot = await ProbeTemperaturesAsync(cancellationToken)
                .ConfigureAwait(false);
            lock (temperatureGate)
            {
                cachedTemperatures = new TemperatureCacheEntry(snapshot, Stopwatch.GetTimestamp());
            }
            return snapshot;
        }
        finally
        {
            temperatureProbeLock.Release();
        }
    }

    private bool TryGetCachedTemperatures(out MacTemperatureSnapshot snapshot)
    {
        lock (temperatureGate)
        {
            if (cachedTemperatures is { } entry &&
                Stopwatch.GetElapsedTime(entry.StoredAt) <= TemperatureCacheDuration)
            {
                snapshot = entry.Snapshot;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    private static async Task<MacTemperatureSnapshot> ProbeTemperaturesAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return MacTemperatureSnapshot.Unavailable(
                "Temperature sensors are only available from this service on macOS.",
                "macOS");
        }

        MacTemperatureSnapshot native = await ProbeTemperatureBridgeAsync(cancellationToken)
            .ConfigureAwait(false);
        MetricSample cpu = native.Cpu;
        MetricSample disk = native.Disk;

        if (!cpu.Available)
        {
            MetricSample fallback = await ProbeCpuTemperatureToolAsync(cancellationToken)
                .ConfigureAwait(false);
            cpu = PreferAvailableOrCombine(cpu, fallback);
        }
        if (!disk.Available)
        {
            MetricSample fallback = await ProbeDiskTemperatureToolAsync(cancellationToken)
                .ConfigureAwait(false);
            disk = PreferAvailableOrCombine(disk, fallback);
        }

        return new MacTemperatureSnapshot(cpu, native.Gpu, disk);
    }

    private static async Task<MacTemperatureSnapshot> ProbeTemperatureBridgeAsync(
        CancellationToken cancellationToken)
    {
        string? bridge = ResolveBridge();
        if (bridge is null)
        {
            return MacTemperatureSnapshot.Unavailable(
                "PicoVibeMacBridge is missing from the application bundle.",
                "PicoVibeMacBridge");
        }

        ProcessResult result = await RunAsync(bridge, "temperatures", cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0
            ? ParseTemperatureBridgeOutput(result.Output)
            : MacTemperatureSnapshot.Unavailable(
                "PicoVibeMacBridge could not read the macOS temperature sensors.",
                "PicoVibeMacBridge");
    }

    private static MetricSample PreferAvailableOrCombine(
        MetricSample primary,
        MetricSample fallback)
    {
        if (fallback.Available)
        {
            return fallback;
        }

        string reason = string.Join(
            " ",
            new[] { primary.Reason, fallback.Reason }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));
        return MetricSample.Unavailable(reason, "macOS temperature providers");
    }

    private static async Task<MetricSample> ProbeCpuTemperatureToolAsync(
        CancellationToken cancellationToken)
    {
        foreach (string path in new[] { "/opt/homebrew/bin/osx-cpu-temp", "/usr/local/bin/osx-cpu-temp" })
        {
            if (!File.Exists(path))
            {
                continue;
            }
            var result = await RunAsync(path, string.Empty, cancellationToken).ConfigureAwait(false);
            var match = TemperatureRegex().Match(result.Output);
            if (result.ExitCode == 0 && match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value))
            {
                return MetricSample.Success(value, path);
            }
        }

        return MetricSample.Unavailable(
            "CPU temperature needs the optional osx-cpu-temp utility; no privileged helper is bundled.",
            "macOS");
    }

    private static async Task<MetricSample> ProbeDiskTemperatureToolAsync(
        CancellationToken cancellationToken)
    {
        foreach (string path in new[] { "/opt/homebrew/sbin/smartctl", "/usr/local/sbin/smartctl" })
        {
            if (!File.Exists(path))
            {
                continue;
            }
            var result = await RunAsync(path, "-a /dev/disk0", cancellationToken).ConfigureAwait(false);
            foreach (string line in result.Output.Split('\n'))
            {
                if (!line.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var values = NumberRegex().Matches(line);
                if (values.Count > 0 &&
                    double.TryParse(values[^1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value) && value is > 0 and < 150)
                {
                    return MetricSample.Success(value, path);
                }
            }
        }

        return MetricSample.Unavailable(
            "Disk temperature needs an accessible smartctl installation and supported drive.",
            "macOS");
    }

    private async Task<MetricSample> GetBatteryLevelAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return MetricSample.Unavailable(
                "Battery level is only available from this service on macOS.",
                "macOS pmset");
        }

        if (TryGetCachedBattery(out MetricSample cached))
        {
            return cached;
        }

        await batteryProbeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedBattery(out cached))
            {
                return cached;
            }

            const string path = "/usr/bin/pmset";
            MetricSample sample;
            if (!File.Exists(path))
            {
                sample = MetricSample.Unavailable(
                    "The macOS power-status utility was not found.",
                    "macOS pmset");
            }
            else
            {
                ProcessResult result = await RunAsync(path, "-g batt", cancellationToken)
                    .ConfigureAwait(false);
                sample = result.ExitCode == 0
                    ? ParseBatteryOutput(result.Output)
                    : MetricSample.Unavailable(
                        "macOS could not read the internal battery status.",
                        "macOS pmset");
            }

            lock (batteryGate)
            {
                cachedBattery = new CacheEntry(sample, Stopwatch.GetTimestamp());
            }

            return sample;
        }
        finally
        {
            batteryProbeLock.Release();
        }
    }

    internal static MetricSample ParseBatteryOutput(string? output)
    {
        string normalizedOutput = output ?? string.Empty;
        Match match = BatteryRegex().Match(normalizedOutput);
        if (!match.Success ||
            !int.TryParse(match.Groups["percent"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out int percent) ||
            percent is < 0 or > 100)
        {
            return MetricSample.Unavailable(
                "No internal battery was reported by macOS.",
                "macOS pmset");
        }

        bool drawingFromAcPower = normalizedOutput.Contains(
            "drawing from 'AC Power'",
            StringComparison.OrdinalIgnoreCase);
        string state = DescribeBatteryState(match.Groups["state"].Value, drawingFromAcPower);
        return MetricSample.Success(percent, $"macOS pmset ({state})");
    }

    internal static MacTemperatureSnapshot ParseTemperatureBridgeOutput(string? output)
    {
        string[] lines = (output ?? string.Empty).Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string line in lines.Reverse())
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("cpu", out _))
                {
                    continue;
                }

                return new MacTemperatureSnapshot(
                    ParseBridgeTemperatureMetric(root, "cpu", "CPU or SoC"),
                    ParseBridgeTemperatureMetric(root, "gpu", "GPU"),
                    ParseBridgeTemperatureMetric(root, "disk", "internal NAND"));
            }
            catch (JsonException)
            {
                // A helper or loader can write a diagnostic line before the
                // one-shot JSON result. Ignore non-JSON lines and keep looking.
            }
        }

        return MacTemperatureSnapshot.Unavailable(
            "PicoVibeMacBridge returned no valid temperature JSON.",
            "PicoVibeMacBridge");
    }

    private static MetricSample ParseBridgeTemperatureMetric(
        JsonElement root,
        string propertyName,
        string displayName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement metric) ||
            metric.ValueKind != JsonValueKind.Object)
        {
            return MetricSample.Unavailable(
                $"PicoVibeMacBridge returned no {displayName} temperature result.",
                "PicoVibeMacBridge");
        }

        string source = metric.TryGetProperty("source", out JsonElement sourceElement) &&
                        sourceElement.ValueKind == JsonValueKind.String
            ? sourceElement.GetString() ?? "macOS IOHID"
            : "macOS IOHID";
        string reason = metric.TryGetProperty("reason", out JsonElement reasonElement) &&
                        reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString() ?? string.Empty
            : string.Empty;

        if (metric.TryGetProperty("value", out JsonElement valueElement) &&
            valueElement.ValueKind == JsonValueKind.Number &&
            valueElement.TryGetDouble(out double value) &&
            double.IsFinite(value) &&
            value is > 0 and < 150)
        {
            return MetricSample.Success(value, source);
        }

        return MetricSample.Unavailable(
            string.IsNullOrWhiteSpace(reason)
                ? $"macOS returned no plausible {displayName} temperature."
                : reason,
            source);
    }

    private bool TryGetCachedBattery(out MetricSample sample)
    {
        lock (batteryGate)
        {
            if (cachedBattery is { } entry &&
                Stopwatch.GetElapsedTime(entry.StoredAt) <= BatteryCacheDuration)
            {
                sample = entry.Sample;
                return true;
            }
        }

        sample = null!;
        return false;
    }

    private static string DescribeBatteryState(string value, bool drawingFromAcPower)
    {
        string state = value.Trim().ToLowerInvariant();
        return state switch
        {
            "charging" or "finishing charge" => "Charging",
            "charged" => "Fully charged",
            "discharging" => drawingFromAcPower ? "Plugged in, discharging" : "On battery",
            "not charging" => "Plugged in, not charging",
            _ => "State unknown",
        };
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
        return candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, output + "\n" + error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessResult(-1, "probe timed out");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new ProcessResult(-1, exception.Message);
        }
    }

    private static bool TryReadCpuTicks(out CpuTicks ticks)
    {
        var values = new uint[4];
        uint count = (uint)values.Length;
        if (HostStatistics(MachHostSelf(), HostCpuLoadInfo, values, ref count) != 0 || count < 4)
        {
            ticks = default;
            return false;
        }
        ticks = new CpuTicks(values[CpuUser], values[CpuSystem], values[CpuIdle], values[CpuNice]);
        return true;
    }

    private static bool TryReadUInt64Sysctl(string name, out ulong value)
    {
        nuint size = sizeof(ulong);
        value = 0;
        return SysctlByName(name, ref value, ref size, IntPtr.Zero, 0) == 0 && size == sizeof(ulong);
    }

    private static ulong Delta(uint current, uint previous) => unchecked(current - previous);

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)")]
    private static partial Regex TemperatureRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"(?<percent>\d{1,3})%;\s*(?<state>[^;\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BatteryRegex();

    [DllImport("libSystem.B.dylib", EntryPoint = "mach_host_self")]
    private static extern uint MachHostSelf();

    [DllImport("libSystem.B.dylib", EntryPoint = "host_statistics")]
    private static extern int HostStatistics(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport("libSystem.B.dylib", EntryPoint = "host_statistics64")]
    private static extern int HostStatistics64(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport("libSystem.B.dylib", EntryPoint = "host_page_size")]
    private static extern int HostPageSize(uint host, out uint pageSize);

    [DllImport("libSystem.B.dylib", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref ulong oldValue,
        ref nuint oldLength,
        IntPtr newValue,
        nuint newLength);

    private readonly record struct CpuTicks(uint User, uint System, uint Idle, uint Nice);
    private readonly record struct ProcessResult(int ExitCode, string Output);
    private sealed record CacheEntry(MetricSample Sample, long StoredAt);
    private sealed record TemperatureCacheEntry(MacTemperatureSnapshot Snapshot, long StoredAt);

    internal sealed record MacTemperatureSnapshot(
        MetricSample Cpu,
        MetricSample Gpu,
        MetricSample Disk)
    {
        public MetricSample For(SystemMetric metric) => metric switch
        {
            SystemMetric.CpuTemperature => Cpu,
            SystemMetric.GpuTemperature => Gpu,
            SystemMetric.DiskTemperature => Disk,
            _ => MetricSample.Unavailable("Unsupported temperature metric.", "macOS"),
        };

        public static MacTemperatureSnapshot Unavailable(string reason, string source)
        {
            MetricSample sample = MetricSample.Unavailable(reason, source);
            return new MacTemperatureSnapshot(sample, sample, sample);
        }
    }
}
