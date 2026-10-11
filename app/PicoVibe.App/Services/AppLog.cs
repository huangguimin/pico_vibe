using System.IO;

namespace PicoVibe.App.Services;

public static class AppLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const long MaximumBytes = 1_000_000;

    /// <summary>
    /// Resolved per call so tests can redirect the log with
    /// PICO_VIBE_TEST_HOME instead of appending to the user's real one, the
    /// same escape hatch the hook installer uses.
    /// </summary>
    public static string FilePath
    {
        get
        {
            var testHome = Environment.GetEnvironmentVariable("PICO_VIBE_TEST_HOME");
            var root = !string.IsNullOrWhiteSpace(testHome) && Path.IsPathFullyQualified(testHome)
                ? testHome
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "PicoVibe", "pico-vibe.log");
        }
    }

    public static void Info(string message) => _ = WriteAsync("INFO", message, null);

    public static void Error(string message, Exception exception) =>
        _ = WriteAsync("ERROR", message, exception);

    /// <summary>
    /// Writes before returning. The normal helpers are fire-and-forget, which
    /// is fine while the process lives, but a shutdown tears it down before the
    /// write runs and the log then says nothing about why the lights stopped.
    /// </summary>
    public static void Fatal(string message, Exception? exception) =>
        WriteNow("FATAL", message, exception);

    /// <summary>Synchronous counterpart of <see cref="Info"/>.</summary>
    public static void InfoNow(string message) => WriteNow("INFO", message, null);

    private static void WriteNow(string level, string message, Exception? exception)
    {
        try
        {
            Gate.Wait();
            try
            {
                Write(level, message, exception);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // Diagnostics must never affect the resident lighting service.
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaximumBytes)
        {
            File.Move(FilePath, FilePath + ".1", overwrite: true);
        }

        var detail = exception is null ? string.Empty : $" | {exception}";
        File.AppendAllText(
            FilePath,
            $"{DateTimeOffset.Now:o} [{level}] {message}{detail}{Environment.NewLine}");
    }

    private static async Task WriteAsync(string level, string message, Exception? exception)
    {
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Write(level, message, exception);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // Diagnostics must never affect the resident lighting service.
        }
    }
}
