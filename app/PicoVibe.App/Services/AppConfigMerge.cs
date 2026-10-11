using System.Text.Json;
using System.Text.Json.Nodes;
using PicoVibe.Core.Configuration;

namespace PicoVibe.App.Services;

/// <summary>
/// Three-way merge used when a device-originated setting arrives while the UI
/// still contains an unsaved draft. Independent remote changes are retained;
/// a same-field conflict keeps the user's draft so typing is never discarded.
/// </summary>
public static class AppConfigMerge
{
    public static AppConfig PreserveLocalDraft(
        AppConfig baseline,
        AppConfig localDraft,
        AppConfig remote)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(localDraft);
        ArgumentNullException.ThrowIfNull(remote);

        var baselineNode = JsonSerializer.SerializeToNode(baseline, ConfigStore.JsonOptions);
        var localNode = JsonSerializer.SerializeToNode(localDraft, ConfigStore.JsonOptions);
        var remoteNode = JsonSerializer.SerializeToNode(remote, ConfigStore.JsonOptions);
        var mergedNode = MergeNode(baselineNode, localNode, remoteNode);
        return AppConfigDefaults.Normalize(
            mergedNode?.Deserialize<AppConfig>(ConfigStore.JsonOptions));
    }

    private static JsonNode? MergeNode(
        JsonNode? baseline,
        JsonNode? local,
        JsonNode? remote)
    {
        if (JsonNode.DeepEquals(local, baseline))
        {
            return remote?.DeepClone();
        }
        if (JsonNode.DeepEquals(remote, baseline) || JsonNode.DeepEquals(local, remote))
        {
            return local?.DeepClone();
        }

        if (baseline is JsonObject baselineObject &&
            local is JsonObject localObject &&
            remote is JsonObject remoteObject)
        {
            var merged = new JsonObject();
            var names = baselineObject.Select(pair => pair.Key)
                .Concat(localObject.Select(pair => pair.Key))
                .Concat(remoteObject.Select(pair => pair.Key))
                .Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                baselineObject.TryGetPropertyValue(name, out var baselineValue);
                localObject.TryGetPropertyValue(name, out var localValue);
                remoteObject.TryGetPropertyValue(name, out var remoteValue);
                merged[name] = MergeNode(baselineValue, localValue, remoteValue);
            }
            return merged;
        }

        // Both sides changed the same scalar/array. Protect the in-progress UI
        // draft; its eventual autosave will become the new authoritative value.
        return local?.DeepClone();
    }
}
