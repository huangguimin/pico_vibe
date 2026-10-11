using System.IO;
using System.Text.Json;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Ipc;
using PicoVibe.Core.Lighting;

namespace PicoVibe.App.Services;

public sealed record AgentLampView(IReadOnlyList<AgentLamp> Lamps, byte[] Frame, int Capacity, string? TouchAgentKey);

public sealed partial class LightOrchestrator
{
    // Only an actual agent frame uses the agent cadence; merely enabling
    // permission overlays must not slow music or system-state frames.
    internal static TimeSpan RequestedFrameInterval(AppConfig snapshot, bool perAgentFrame) => perAgentFrame
        ? TimeSpan.FromMilliseconds(100)
        : snapshot.Global.ActiveMode == PrimaryMode.Music
        ? TimeSpan.FromSeconds(1.0 / Math.Clamp(snapshot.Music.FramesPerSecond, 1, 30))
        : TimeSpan.FromMilliseconds(Math.Clamp(snapshot.SystemMonitor.UpdateIntervalMilliseconds, 100, 5000));

    private readonly AgentLampLayout agentLampLayout = new();
    private string agentLampPath = "";
    private long persistedAgentRevision = -1;
    private string? locateAgentKey;
    private DateTimeOffset locateUntil;

    private void InitializeAgentLamps()
    {
        agentLampPath = Path.Combine(Path.GetDirectoryName(configStore.FilePath)!, "agent-lamps.json");
        try
        {
            if (File.Exists(agentLampPath))
                agentLampLayout.Restore(JsonSerializer.Deserialize<AgentLampLease[]>(File.ReadAllText(agentLampPath), ConfigStore.JsonOptions)
                    ?? Array.Empty<AgentLampLease>());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { AppLog.Error("Restoring agent lamp assignments failed.", exception); }
    }

    private int AgentLampCapacityLocked() => Math.Clamp(Math.Min(config.Global.LedCount,
        deviceInfo.LedCount > 0 ? deviceInfo.LedCount : 12), 1, 12);

    private IReadOnlyList<AgentLamp> UpdateAgentLampsLocked(DateTimeOffset now)
    {
        var agents = aiTracker.GetAgents(now, config.Ai).Select(a =>
            config.Ai.MultiAgent.AgentNames.TryGetValue(a.Key, out var name) && !string.IsNullOrWhiteSpace(name)
                ? a with { Name = name } : a).ToArray();
        var touchKey = pendingPermission is { } pending && !pending.Completion.Task.IsCompleted
            ? AgentSnapshot.KeyFor(pending.Request) : null;
        return agentLampLayout.Sync(agents, AgentLampCapacityLocked(), now, config.Ai.DoneOverlayMilliseconds)
            .Select(l => l with { TouchReady = l.Agent.Key == touchKey }).ToArray();
    }

    public AgentLampView GetAgentLampView()
    {
        lock (stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            var lamps = UpdateAgentLampsLocked(now);
            var capacity = AgentLampCapacityLocked();
            var frame = AgentFrameRenderer.Render(lamps, config.Ai.MultiAgent, config.Global, now, capacity,
                now < locateUntil ? locateAgentKey : null);
            return new(lamps, frame, capacity, lamps.FirstOrDefault(l => l.TouchReady)?.Agent.Key);
        }
    }

    public bool LocateAgent(string key)
    {
        lock (stateGate)
        {
            if (!config.Ai.Enabled || config.Global.ActiveMode != PrimaryMode.Ai || config.Ai.DisplayMode != AiDisplayMode.PerAgent)
                return false;
            var lamps = UpdateAgentLampsLocked(DateTimeOffset.UtcNow);
            if (lamps.Any(l => l.TouchReady || l.Agent.State == AiState.Permission) || !lamps.Any(l => l.Agent.Key == key && l.Slot is not null)) return false;
            locateAgentKey = key;
            locateUntil = DateTimeOffset.UtcNow.AddSeconds(1.2);
            return true;
        }
    }

    private async Task PersistAgentLampsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentLampLease> leases;
        long revision;
        lock (stateGate)
        {
            UpdateAgentLampsLocked(DateTimeOffset.UtcNow);
            revision = agentLampLayout.Revision;
            if (revision == persistedAgentRevision) return;
            leases = agentLampLayout.Save();
        }
        var temporary = agentLampPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(agentLampPath)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(leases, ConfigStore.JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, agentLampPath, overwrite: true);
            persistedAgentRevision = revision;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { AppLog.Error("Saving agent lamp assignments failed.", exception); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
