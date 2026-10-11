using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using PicoVibe.Core.Configuration;

namespace PicoVibe.App.Services;

/// <summary>
/// Reads Windows system metrics without installing a hardware-access driver.
/// Temperature probes are deliberately best-effort and cached because their
/// backing providers are relatively slow and are not present on every machine.
/// </summary>
public sealed class SystemMetricService : ISystemMetricService
{
    private static readonly TimeSpan CpuTemperatureCacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GpuTemperatureCacheDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiskTemperatureCacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExternalProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly object _cpuGate = new();
    private readonly object _cacheGate = new();
    private readonly Dictionary<SystemMetric, CacheEntry> _temperatureCache = new();
    private readonly Dictionary<SystemMetric, SemaphoreSlim> _temperatureProbeLocks = new()
    {
        [SystemMetric.CpuTemperature] = new(1, 1),
        [SystemMetric.GpuTemperature] = new(1, 1),
        [SystemMetric.DiskTemperature] = new(1, 1),
    };

    private CpuTimes? _previousCpuTimes;

    public SystemMetricService()
    {
        // Prime the delta-based CPU counter. A caller that samples immediately can
        // still receive "warming up"; the next normal UI tick will be meaningful.
        if (TryReadCpuTimes(out CpuTimes times))
        {
            _previousCpuTimes = times;
        }
    }

    /// <summary>
    /// Gets one metric. Values for usage metrics and temperatures are percentages
    /// and degrees Celsius respectively.
    /// </summary>
    public async Task<MetricSample> GetSampleAsync(
        SystemMetric metric,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return metric switch
            {
                SystemMetric.CpuUsage => GetCpuUsage(),
                SystemMetric.MemoryUsage => GetMemoryUsage(),
                SystemMetric.CpuTemperature => await GetCachedTemperatureAsync(
                    metric,
                    CpuTemperatureCacheDuration,
                    ProbeCpuTemperatureAsync,
                    cancellationToken).ConfigureAwait(false),
                SystemMetric.GpuTemperature => await GetCachedTemperatureAsync(
                    metric,
                    GpuTemperatureCacheDuration,
                    ProbeNvidiaGpuTemperatureAsync,
                    cancellationToken).ConfigureAwait(false),
                SystemMetric.DiskTemperature => await GetCachedTemperatureAsync(
                    metric,
                    DiskTemperatureCacheDuration,
                    ProbeDiskTemperatureAsync,
                    cancellationToken).ConfigureAwait(false),
                SystemMetric.BatteryLevel => GetBatteryLevel(),
                _ => MetricSample.Unavailable("Unsupported metric.", "PicoVibe metric service"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Hardware telemetry must never take down the UI or the lighting loop.
            return MetricSample.Unavailable(
                $"Metric probe failed: {GetSafeExceptionMessage(exception)}",
                GetExpectedSource(metric));
        }
    }

    private MetricSample GetCpuUsage()
    {
        lock (_cpuGate)
        {
            if (!TryReadCpuTimes(out CpuTimes current))
            {
                return MetricSample.Unavailable(
                    "Windows could not read system CPU times.",
                    "Windows GetSystemTimes");
            }

            CpuTimes? previous = _previousCpuTimes;
            _previousCpuTimes = current;

            if (previous is null ||
                current.Idle < previous.Value.Idle ||
                current.Kernel < previous.Value.Kernel ||
                current.User < previous.Value.User)
            {
                return MetricSample.Unavailable(
                    "CPU utilization is warming up; sample again on the next tick.",
                    "Windows GetSystemTimes");
            }

            ulong idleDelta = current.Idle - previous.Value.Idle;
            ulong kernelDelta = current.Kernel - previous.Value.Kernel;
            ulong userDelta = current.User - previous.Value.User;
            ulong totalDelta = kernelDelta + userDelta;

            if (totalDelta == 0 || idleDelta > totalDelta)
            {
                return MetricSample.Unavailable(
                    "Not enough elapsed CPU time to calculate utilization.",
                    "Windows GetSystemTimes");
            }

            double busy = totalDelta - idleDelta;
            double percent = Math.Clamp((busy / totalDelta) * 100.0, 0.0, 100.0);
            return MetricSample.Success(percent, "Windows GetSystemTimes");
        }
    }

    private static MetricSample GetMemoryUsage()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
        };

        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            return MetricSample.Unavailable(
                "Windows could not read physical memory status.",
                "Windows GlobalMemoryStatusEx");
        }

        double used = status.TotalPhysical - Math.Min(status.AvailablePhysical, status.TotalPhysical);
        double percent = Math.Clamp((used / status.TotalPhysical) * 100.0, 0.0, 100.0);
        return MetricSample.Success(percent, "Windows GlobalMemoryStatusEx");
    }

    private static MetricSample GetBatteryLevel()
    {
        const string source = "Windows GetSystemPowerStatus";
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out SystemPowerStatus status))
        {
            return MetricSample.Unavailable(
                "Windows could not read the system battery status.",
                source);
        }

        const byte unknownValue = byte.MaxValue;
        const byte noSystemBattery = 0x80;
        if (status.BatteryLifePercent == unknownValue ||
            status.BatteryFlag == unknownValue ||
            (status.BatteryFlag & noSystemBattery) != 0)
        {
            return MetricSample.Unavailable(
                "No system battery was reported by Windows.",
                source);
        }

        double percent = Math.Clamp((double)status.BatteryLifePercent, 0.0, 100.0);
        return MetricSample.Success(percent, $"{source} ({DescribeBatteryState(status, percent)})");
    }

    private static string DescribeBatteryState(SystemPowerStatus status, double percent)
    {
        const byte charging = 0x08;
        const byte low = 0x02;
        const byte critical = 0x04;
        if ((status.BatteryFlag & charging) != 0)
        {
            return "Charging";
        }

        if (status.AcLineStatus == 0)
        {
            if ((status.BatteryFlag & critical) != 0)
            {
                return "On battery, critical";
            }

            if ((status.BatteryFlag & low) != 0)
            {
                return "On battery, low";
            }

            return "On battery";
        }

        if (status.AcLineStatus == 1)
        {
            return percent >= 100.0 ? "Fully charged" : "Plugged in, not charging";
        }

        return "State unknown";
    }

    private async Task<MetricSample> GetCachedTemperatureAsync(
        SystemMetric metric,
        TimeSpan maximumAge,
        Func<CancellationToken, Task<MetricSample>> probe,
        CancellationToken cancellationToken)
    {
        if (TryGetFreshCacheEntry(metric, maximumAge, out MetricSample cached))
        {
            return cached;
        }

        SemaphoreSlim probeLock = _temperatureProbeLocks[metric];
        await probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFreshCacheEntry(metric, maximumAge, out cached))
            {
                return cached;
            }

            MetricSample sample = await probe(cancellationToken).ConfigureAwait(false);
            lock (_cacheGate)
            {
                _temperatureCache[metric] = new CacheEntry(sample, Stopwatch.GetTimestamp());
            }

            return sample;
        }
        finally
        {
            probeLock.Release();
        }
    }

    private bool TryGetFreshCacheEntry(
        SystemMetric metric,
        TimeSpan maximumAge,
        out MetricSample sample)
    {
        lock (_cacheGate)
        {
            if (_temperatureCache.TryGetValue(metric, out CacheEntry? entry) &&
                Stopwatch.GetElapsedTime(entry.StoredAt) <= maximumAge)
            {
                sample = entry.Sample;
                return true;
            }
        }

        sample = null!;
        return false;
    }

    private static async Task<MetricSample> ProbeCpuTemperatureAsync(
        CancellationToken cancellationToken)
    {
        const string source =
            "Windows ACPI MSAcpi_ThermalZoneTemperature (thermal zone, not CPU package/core)";
        const string script =
            "$ErrorActionPreference='Stop'; " +
            "Get-CimInstance -Namespace 'root/wmi' -ClassName 'MSAcpi_ThermalZoneTemperature' " +
            "-ErrorAction Stop | ForEach-Object { " +
            "[Console]::WriteLine([Convert]::ToString($_.CurrentTemperature, " +
            "[Globalization.CultureInfo]::InvariantCulture)) }";

        ProcessResult result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return MetricSample.Unavailable("The ACPI temperature query timed out.", source);
        }

        if (result.ExitCode != 0)
        {
            return MetricSample.Unavailable(
                "Windows did not expose a usable ACPI thermal zone; CPU package temperature is unavailable.",
                source);
        }

        var temperatures = new List<double>();
        foreach (string line in SplitOutputLines(result.StandardOutput))
        {
            if (!double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double tenthsKelvin))
            {
                continue;
            }

            double celsius = (tenthsKelvin / 10.0) - 273.15;
            if (IsPlausibleTemperature(celsius))
            {
                temperatures.Add(celsius);
            }
        }

        return temperatures.Count == 0
            ? MetricSample.Unavailable(
                "Windows did not return a plausible ACPI thermal-zone value; CPU package temperature is unavailable.",
                source)
            : MetricSample.Success(temperatures.Max(), source);
    }

    private static async Task<MetricSample> ProbeNvidiaGpuTemperatureAsync(
        CancellationToken cancellationToken)
    {
        const string source = "NVIDIA NVML via nvidia-smi";
        var startInfo = CreateHiddenProcessStartInfo(ResolveNvidiaSmiPath());
        startInfo.ArgumentList.Add("--query-gpu=index,temperature.gpu");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        ProcessResult result;
        try
        {
            result = await RunProcessAsync(
                startInfo,
                ExternalProbeTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return MetricSample.Unavailable(
                "NVIDIA telemetry is unavailable; nvidia-smi or a supported NVIDIA driver was not found.",
                source);
        }

        if (result.TimedOut)
        {
            return MetricSample.Unavailable("The NVIDIA temperature query timed out.", source);
        }

        if (result.ExitCode != 0)
        {
            return MetricSample.Unavailable(
                AddProcessDetail("NVIDIA did not return a GPU temperature.", result.StandardError),
                source);
        }

        double? hottest = null;
        string? hottestIndex = null;
        foreach (string line in SplitOutputLines(result.StandardOutput))
        {
            string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 2 ||
                !double.TryParse(fields[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !IsPlausibleTemperature(value))
            {
                continue;
            }

            if (hottest is null || value > hottest.Value)
            {
                hottest = value;
                hottestIndex = fields[0];
            }
        }

        return hottest is null
            ? MetricSample.Unavailable(
                "NVIDIA reported no supported GPU temperature sensor.",
                source)
            : MetricSample.Success(hottest.Value, $"{source} (GPU {hottestIndex})");
    }

    private static async Task<MetricSample> ProbeDiskTemperatureAsync(
        CancellationToken cancellationToken)
    {
        const string source = "Windows Storage Reliability Counter";
        const string script =
            "$ErrorActionPreference='Stop'; $found=$false; " +
            "Get-PhysicalDisk -ErrorAction Stop | ForEach-Object { $disk=$_; try { " +
            "$counter=$disk | Get-StorageReliabilityCounter -ErrorAction Stop; " +
            "if ($null -ne $counter.Temperature) { $found=$true; " +
            "$temperature=[Convert]::ToString($counter.Temperature, " +
            "[Globalization.CultureInfo]::InvariantCulture); " +
            "[Console]::WriteLine($temperature + \"`t\" + $disk.FriendlyName) } } catch {} }; " +
            "if (-not $found) { exit 3 }";

        ProcessResult result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return MetricSample.Unavailable("The storage temperature query timed out.", source);
        }

        if (result.ExitCode != 0)
        {
            return MetricSample.Unavailable(
                "No disk temperature was exposed. The drive/USB bridge may not support it, or Windows denied access.",
                source);
        }

        double? hottest = null;
        string? hottestDisk = null;
        foreach (string line in SplitOutputLines(result.StandardOutput))
        {
            string[] fields = line.Split('\t', 2, StringSplitOptions.TrimEntries);
            if (fields.Length == 0 ||
                !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !IsPlausibleTemperature(value))
            {
                continue;
            }

            if (hottest is null || value > hottest.Value)
            {
                hottest = value;
                hottestDisk = fields.Length > 1 ? fields[1] : null;
            }
        }

        string resolvedSource = string.IsNullOrWhiteSpace(hottestDisk)
            ? source
            : $"{source} ({LimitText(hottestDisk, 80)})";

        return hottest is null
            ? MetricSample.Unavailable(
                "Windows returned no plausible disk temperature.",
                source)
            : MetricSample.Success(hottest.Value, resolvedSource);
    }

    private static Task<ProcessResult> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateHiddenProcessStartInfo(ResolveWindowsPowerShellPath());
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
            "$OutputEncoding=[Console]::OutputEncoding; " +
            script);
        return RunProcessAsync(startInfo, ExternalProbeTimeout, cancellationToken);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = startInfo,
        };

        if (!process.Start())
        {
            return new ProcessResult(-1, string.Empty, "The process could not be started.", false);
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            return new ProcessResult(
                -1,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false),
                true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false),
            false);
    }

    private static ProcessStartInfo CreateHiddenProcessStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8,
    };

    private static string ResolveWindowsPowerShellPath()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string bundledPath = Path.Combine(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(bundledPath) ? bundledPath : "powershell.exe";
    }

    private static string ResolveNvidiaSmiPath()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string systemPath = Path.Combine(windowsDirectory, "System32", "nvidia-smi.exe");
        if (File.Exists(systemPath))
        {
            return systemPath;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string nvSmiPath = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        return File.Exists(nvSmiPath) ? nvSmiPath : "nvidia-smi.exe";
    }

    private static bool TryReadCpuTimes(out CpuTimes times)
    {
        if (!GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user))
        {
            times = default;
            return false;
        }

        times = new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
        return true;
    }

    private static bool IsPlausibleTemperature(double value) =>
        double.IsFinite(value) && value is >= -40.0 and <= 150.0;

    private static IEnumerable<string> SplitOutputLines(string value) =>
        value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string AddProcessDetail(string prefix, string standardError)
    {
        string? detail = SplitOutputLines(standardError).FirstOrDefault();
        return string.IsNullOrWhiteSpace(detail)
            ? prefix
            : $"{prefix} ({LimitText(detail, 180)})";
    }

    private static string GetSafeExceptionMessage(Exception exception) =>
        LimitText(string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message, 180);

    private static string LimitText(string value, int maximumLength)
    {
        string normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumLength - 1), "…");
    }

    private static string GetExpectedSource(SystemMetric metric) => metric switch
    {
        SystemMetric.CpuUsage => "Windows GetSystemTimes",
        SystemMetric.MemoryUsage => "Windows GlobalMemoryStatusEx",
        SystemMetric.CpuTemperature => "Windows ACPI MSAcpi_ThermalZoneTemperature",
        SystemMetric.GpuTemperature => "NVIDIA NVML via nvidia-smi",
        SystemMetric.DiskTemperature => "Windows Storage Reliability Counter",
        SystemMetric.BatteryLevel => "Windows GetSystemPowerStatus",
        _ => "PicoVibe metric service",
    };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    private sealed record CacheEntry(MetricSample Sample, long StoredAt);

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);
}
