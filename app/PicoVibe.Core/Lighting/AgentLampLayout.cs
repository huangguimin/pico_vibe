using PicoVibe.Core.Configuration;
using PicoVibe.Core.Ipc;

namespace PicoVibe.Core.Lighting;

public sealed record AgentLamp(AgentSnapshot Agent, int? Slot, bool TouchReady = false);
public sealed record AgentLampLease(string Key, int Slot, string Name, HookPlatform Platform, DateTimeOffset LastActivity);

/// <summary>Stable logical slots, independent of turns and physical direction.</summary>
public sealed class AgentLampLayout
{
    private readonly Dictionary<string, AgentLampLease> leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentLampLease> preferences = new(StringComparer.Ordinal);
    public long Revision { get; private set; }
    public IReadOnlyList<AgentLampLease> Save() => preferences.Values.OrderByDescending(p => p.LastActivity).Take(128).ToArray();

    public void Restore(IEnumerable<AgentLampLease> saved)
    {
        foreach (var lease in saved)
            if (lease is not null && !string.IsNullOrWhiteSpace(lease.Key) && lease.Slot is >= 0 and < 12)
                preferences.TryAdd(lease.Key, lease);
    }

    public IReadOnlyList<AgentLamp> Sync(IReadOnlyList<AgentSnapshot> agents, int capacity, DateTimeOffset now, int doneMilliseconds)
    {
        capacity = Math.Clamp(capacity, 1, 12);
        var current = agents.ToDictionary(a => a.Key, StringComparer.Ordinal);
        foreach (var lease in leases.Values.ToArray())
        {
            if (lease.Slot >= capacity) { Remove(lease.Key); continue; }
            if (!current.TryGetValue(lease.Key, out var agent) || agent.Ended)
            {
                Remove(lease.Key);
            }
            else
            {
                var updated = lease with { Name = agent.Name, LastActivity = agent.LastActivity };
                if (updated != lease) { leases[lease.Key] = updated; preferences[lease.Key] = updated; Revision++; }
            }
        }

        foreach (var agent in agents.Where(a => !a.Ended && a.State != AiState.Idle).OrderBy(a => a.LastActivity).ThenBy(a => a.Key, StringComparer.Ordinal))
        {
            if (leases.ContainsKey(agent.Key)) continue;
            if (leases.Count < capacity)
            {
                var slot = preferences.TryGetValue(agent.Key, out var preferred) && preferred.Slot < capacity &&
                    leases.Values.All(l => l.Slot != preferred.Slot) ? preferred.Slot :
                    Enumerable.Range(0, capacity).First(i => leases.Values.All(l => l.Slot != i));
                leases.Add(agent.Key, new(agent.Key, slot, agent.Name, agent.Platform, agent.LastActivity));
                preferences[agent.Key] = leases[agent.Key];
                Revision++;
            }
        }

        var result = agents.Where(a => !a.Ended)
            .Select(a => new AgentLamp(a, leases.TryGetValue(a.Key, out var lease) ? lease.Slot : null)).ToList();
        return result.OrderBy(l => l.Slot ?? int.MaxValue).ThenBy(l => l.Agent.Key, StringComparer.Ordinal).ToArray();
    }

    private void Remove(string key)
    {
        if (leases.Remove(key)) Revision++;
    }
}
