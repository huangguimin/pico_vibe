using System.Text.Json;
using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Ipc;

/// <summary>
/// Local, bounded display-event spool. No prompts, commands, outputs or approval
/// capabilities are stored. Replaying PermissionRequested only restores a light.
/// Atomic, unique files allow concurrent short-lived hook processes to publish.
/// </summary>
public sealed class HookEventJournal(string directory, DateTimeOffset? notBefore = null)
{
    private readonly HashSet<string> read = new(StringComparer.Ordinal);
    public static string DefaultDirectory => Path.Combine(
        Path.GetDirectoryName(ConfigPaths.DefaultConfigPath)!, "hook-events");

    public void Append(HookRequest request)
    {
        Directory.CreateDirectory(directory);
        var name = $"{request.Timestamp.UtcTicks:D19}-{Guid.NewGuid():N}.json";
        var target = Path.Combine(directory, name);
        var temporary = target + ".tmp";
        try
        {
            File.WriteAllText(temporary, HookJson.Serialize(request with { ObserveOnly = true }));
            File.Move(temporary, target);
            // Bound storage even when the desktop app is not running.
            var files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
            foreach (var obsolete in files.Take(Math.Max(0, files.Length - 8192))) File.Delete(obsolete);
        }
        finally { File.Delete(temporary); }
    }

    public IReadOnlyList<HookRequest> ReadNew(DateTimeOffset now)
    {
        if (!Directory.Exists(directory)) return Array.Empty<HookRequest>();
        var files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
        read.IntersectWith(files);
        var result = new List<HookRequest>();
        for (var i = 0; i < files.Length; i++)
        {
            var path = files[i];
            try
            {
                if (i < files.Length - 8192 || File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddDays(-1))
                {
                    File.Delete(path);
                    read.Remove(path);
                    continue;
                }
                if (read.Contains(path)) continue;
                if (new FileInfo(path).Length > 16384) { read.Add(path); continue; }
                var item = HookJson.Deserialize<HookRequest>(File.ReadAllText(path));
                if (item is not null && item.Protocol == HookRequest.CurrentProtocol &&
                    !item.Metadata.ContainsKey("control") &&
                    (notBefore is null || item.Timestamp >= notBefore.Value)) result.Add(item);
                read.Add(path);
            }
            catch (JsonException) { read.Add(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderBy(e => e.Timestamp).ToArray();
    }
}
