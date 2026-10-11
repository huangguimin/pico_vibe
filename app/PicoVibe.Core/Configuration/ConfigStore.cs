using System.Text.Json;
using System.Text.Json.Serialization;

namespace PicoVibe.Core.Configuration;

public static class ConfigPaths
{
    public static string DefaultConfigPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(local, "PicoVibe", "config.json");
        }
    }
}

/// <summary>Loads and atomically replaces the user's versioned JSON configuration.</summary>
public sealed class ConfigStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public ConfigStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? ConfigPaths.DefaultConfigPath);
    }

    public string FilePath { get; }

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return AppConfig.CreateDefault();
            }

            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return AppConfigDefaults.Normalize(config);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            AppConfigDefaults.Normalize(config);
            var directory = Path.GetDirectoryName(FilePath)
                ?? throw new InvalidOperationException("Configuration path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    config,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath) && OperatingSystem.IsWindows())
            {
                File.Replace(temporary, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, FilePath, overwrite: true);
            }
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // Preserve the original exception. A stale uniquely named
                    // temp file is safer than deleting the live configuration.
                }
            }

            gate.Release();
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
