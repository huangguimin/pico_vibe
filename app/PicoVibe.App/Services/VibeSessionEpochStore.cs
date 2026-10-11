using System.Globalization;
using System.IO;

namespace PicoVibe.App.Services;

/// <summary>
/// Allocates RFC-1982 ordered VIBE session epochs. The value is persisted
/// independently from the editable UI configuration so restarting the app
/// cannot randomly move backwards relative to a still-running Pico.
/// </summary>
public sealed class VibeSessionEpochStore
{
    private static string MutexName => OperatingSystem.IsWindows()
        ? "Local\\PicoVibe.VibeEpoch.v1"
        : "PicoVibe.VibeEpoch.v1";

    public VibeSessionEpochStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? DefaultFilePath);
    }

    public string FilePath { get; }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PicoVibe",
        "vibe-epoch.txt");

    public uint AllocateNext()
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        var ownsMutex = WaitForMutex(mutex);
        try
        {
            var previous = ReadPersistedValue();
            var next = previous is { } value
                ? Increment(value)
                : InitialValue();
            WritePersistedValue(next);
            return next;
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Keeps <paramref name="candidate"/> when it is newer than the epoch
    /// observed in the device mailboxes; otherwise advances directly past the
    /// device value and persists the migration before any packet is written.
    /// </summary>
    public uint EnsureNewerThan(uint candidate, uint deviceEpoch)
    {
        if (candidate == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate));
        }
        if (deviceEpoch == 0)
        {
            return candidate;
        }

        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        var ownsMutex = WaitForMutex(mutex);
        try
        {
            var persisted = ReadPersistedValue();
            var current = persisted is { } value && IsSerialNewer(value, candidate)
                ? value
                : candidate;
            if (IsSerialNewer(current, deviceEpoch))
            {
                if (persisted != current)
                {
                    WritePersistedValue(current);
                }
                return current;
            }

            var next = Increment(deviceEpoch);
            WritePersistedValue(next);
            return next;
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public static bool IsSerialNewer(uint candidate, uint current)
    {
        var delta = unchecked(candidate - current);
        return delta != 0 && delta < 0x8000_0000u;
    }

    private static bool WaitForMutex(Mutex mutex)
    {
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new IOException("Timed out while allocating a VIBE session epoch.");
            }
        }
        catch (AbandonedMutexException)
        {
            // The abandoned mutex is acquired by the current thread.
        }

        return true;
    }

    private uint? ReadPersistedValue()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var text = File.ReadAllText(FilePath).Trim();
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value != 0
            ? value
            : null;
    }

    private void WritePersistedValue(uint value)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("VIBE epoch path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream,
                new System.Text.UTF8Encoding(false),
                bufferSize: 128,
                leaveOpen: true))
            {
                writer.Write(value.ToString(CultureInfo.InvariantCulture));
                writer.Flush();
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
            temporary = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static uint Increment(uint value)
    {
        var next = unchecked(value + 1);
        return next == 0 ? 1u : next;
    }

    private static uint InitialValue()
    {
        var value = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return value == 0 ? 1u : value;
    }
}
