namespace PicoVibe.Core.Device;

public interface IPicoVolumeLocator
{
    ValueTask<PicoDeviceLocation?> FindAsync(
        string? preferredPath = null,
        CancellationToken cancellationToken = default);
}

public sealed class PicoRamLocatorOptions
{
    public string VolumeLabel { get; set; } = "PICO_RGB";
    public IReadOnlyList<string> LegacyVolumeLabels { get; set; } = new[] { "PICO_RAM" };
    public IReadOnlyList<string> AdditionalSearchRoots { get; set; } = Array.Empty<string>();
}

/// <summary>Finds PICO_RGB by explicit path, Windows volume label, or mount name.</summary>
public sealed class PicoRamLocator : IPicoVolumeLocator
{
    private static readonly HashSet<string> KnownFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "RGB.INI",
        "RGB.SEQ",
        "LED.BIN",
        "VIBE0.BIN",
        "VIBE1.BIN",
        "TOUCH.TXT",
    };

    private readonly PicoRamLocatorOptions options;

    public PicoRamLocator(PicoRamLocatorOptions? options = null)
    {
        this.options = options ?? new PicoRamLocatorOptions();
    }

    public ValueTask<PicoDeviceLocation?> FindAsync(
        string? preferredPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var preferred = NormalizeCandidate(preferredPath);
            return ValueTask.FromResult(CompatibleLocation(preferred));
        }

        // Caller-supplied roots are deterministic and take precedence over an
        // unrelated currently mounted device. This also makes diagnostics and
        // tests independent from the user's drive letters.
        foreach (var root in options.AdditionalSearchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = FindCompatibleMount(root, cancellationToken);
            if (found is not null)
            {
                return ValueTask.FromResult<PicoDeviceLocation?>(found);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (drive.IsReady &&
                        IsVolumeLabel(drive.VolumeLabel))
                    {
                        var candidate = CompatibleLocation(drive.RootDirectory.FullName);
                        if (candidate is not null)
                        {
                            return ValueTask.FromResult<PicoDeviceLocation?>(candidate);
                        }
                    }
                }
                catch (IOException)
                {
                    // Removable drives can disappear between enumeration and query.
                }
                catch (UnauthorizedAccessException)
                {
                    // Continue searching other drives without elevating the app.
                }
            }
        }

        foreach (var root in EnumerateSearchRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = FindCompatibleMount(root, cancellationToken);
            if (found is not null)
            {
                return ValueTask.FromResult<PicoDeviceLocation?>(found);
            }
        }

        return ValueTask.FromResult<PicoDeviceLocation?>(null);
    }

    private IEnumerable<string> EnumerateSearchRoots()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Volumes";
            yield break;
        }

        if (!OperatingSystem.IsWindows())
        {
            var user = Environment.UserName;
            yield return Path.Combine("/media", user);
            yield return Path.Combine("/run/media", user);
            yield return "/media";
            yield return "/run/media";
            yield return "/mnt";
        }
    }

    private PicoDeviceLocation? FindCompatibleMount(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        if (IsVolumeLabel(Path.GetFileName(Path.TrimEndingDirectorySeparator(root))))
        {
            return CompatibleLocation(root);
        }

        try
        {
            foreach (var first in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsNamedMount(first))
                {
                    var candidate = CompatibleLocation(first);
                    if (candidate is not null)
                    {
                        return candidate;
                    }
                }

                try
                {
                    foreach (var second in Directory.EnumerateDirectories(first))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsNamedMount(second))
                        {
                            var candidate = CompatibleLocation(second);
                            if (candidate is not null)
                            {
                                return candidate;
                            }
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private bool IsNamedMount(string path) =>
        IsVolumeLabel(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));

    private bool IsVolumeLabel(string label) =>
        string.Equals(label, options.VolumeLabel, StringComparison.OrdinalIgnoreCase) ||
        options.LegacyVolumeLabels.Any(legacy =>
            string.Equals(label, legacy, StringComparison.OrdinalIgnoreCase));

    private static PicoDeviceLocation? CompatibleLocation(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var candidate = new PicoDeviceLocation(path);
        return PicoDeviceCompatibility.IsSupported(candidate) ? candidate : null;
    }

    private static string NormalizeCandidate(string input)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(input.Trim()));
        if (KnownFiles.Contains(Path.GetFileName(path)))
        {
            return Path.GetDirectoryName(path) ?? path;
        }

        return path;
    }
}
