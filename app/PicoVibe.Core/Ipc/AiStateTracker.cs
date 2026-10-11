using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Ipc;

/// <summary>
/// A display-only event reducer. Never authorizes a tool. Call under the broker's
/// state lock. Replay in event order makes IPC retries and delayed journal delivery
/// equivalent to live delivery, including a completion arriving before its start.
/// </summary>
public sealed class AiStateTracker(Func<int, long, bool?>? processAlive = null)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private const int MaximumEvents = 8192;
    private readonly Dictionary<string, HookRequest> events = new(StringComparer.Ordinal);
    private readonly Dictionary<(HookPlatform, string, string, string), Session> sessions = new();
    private bool dirty;
    private DateTimeOffset floor = DateTimeOffset.MinValue;

    public bool Apply(HookRequest request)
    {
        if (request.Metadata.ContainsKey("control") || request.Timestamp < floor ||
            !events.TryAdd(request.RequestId, request)) return false;
        if (events.Count > MaximumEvents)
        {
            var oldest = events.Values.MinBy(e => e.Timestamp)!;
            floor = oldest.Timestamp;
            events.Remove(oldest.RequestId);
        }
        dirty = true;
        return true;
    }

    public AiState GetState(DateTimeOffset now, AiSettings settings)
    {
        if (dirty) Rebuild();
        var states = sessions.Values.Where(IsPresent).Select(s => s.State(now, settings)).ToHashSet();
        foreach (var candidate in new[] { AiState.Permission, AiState.Tools, AiState.Thinking,
                     AiState.Unknown, AiState.Error, AiState.Done })
            if (states.Contains(candidate)) return candidate;
        return AiState.Idle;
    }

    public IReadOnlyList<AgentSnapshot> GetAgents(DateTimeOffset now, AiSettings settings)
    {
        if (dirty) Rebuild();
        return sessions.Where(p => IsPresent(p.Value)).GroupBy(p => (p.Key.Item1, p.Key.Item2, p.Key.Item4)).Select(group =>
        {
            var latest = group.MaxBy(p => p.Value.LastEvent).Value;
            var states = group.Select(p => p.Value.State(now, settings)).ToHashSet();
            var state = new[] { AiState.Permission, AiState.Tools, AiState.Thinking, AiState.Unknown, AiState.Error, AiState.Done, AiState.Idle }
                .First(states.Contains);
            var (platform, session, agent) = group.Key;
            var project = group.Select(p => p.Value.ProjectName).LastOrDefault(p => !string.IsNullOrWhiteSpace(p));
            var label = latest.AgentName ?? (agent.Length > 0 ? "子代理" : project);
            var id = agent.Length > 0 ? agent : session;
            var shortId = id.Length > 6 ? id[..6] : id;
            var name = string.Join(" · ", new[] { platform == HookPlatform.Claude ? "Claude" : platform.ToString(), label, shortId }
                .Where(p => !string.IsNullOrWhiteSpace(p)));
            return new AgentSnapshot(AgentSnapshot.KeyFor(platform, session, agent), platform, name,
                state, latest.LastEvent, agent.Length > 0 && group.All(p => p.Value.Closed));
        }).OrderBy(p => p.LastActivity).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
    }

    private bool IsPresent(Session session) => session.OwnerPid is not { } pid || session.OwnerStart is not { } start ||
        (processAlive ?? AgentProcessIdentity.IsAlive)(pid, start) != false;

    private void Rebuild()
    {
        sessions.Clear();
        var endedSessions = new HashSet<(HookPlatform, string)>();
        foreach (var e in events.Values.OrderBy(e => e.Timestamp).ThenBy(e => e.RequestId, StringComparer.Ordinal))
        {
            var sessionId = e.SessionId ?? "legacy";
            var key = (e.Platform, sessionId, e.TurnId ?? "", e.AgentId ?? "");
            if (e.Event == HookEvent.SessionEnded)
            {
                endedSessions.Add((e.Platform, sessionId));
                foreach (var match in sessions.Keys.Where(k => k.Item1 == e.Platform && k.Item2 == sessionId).ToArray())
                    sessions.Remove(match);
                continue;
            }
            // SessionStart also fires for compaction/resume; it must not reset work.
            if (e.Event == HookEvent.SessionStarted)
            {
                endedSessions.Remove((e.Platform, sessionId));
                continue;
            }
            if (e.Event == HookEvent.PromptSubmitted) endedSessions.Remove((e.Platform, sessionId));
            if (endedSessions.Contains((e.Platform, sessionId))) continue;
            if (!sessions.TryGetValue(key, out var s)) sessions[key] = s = new Session();
            if (e.Event == HookEvent.PromptSubmitted)
            {
                foreach (var match in sessions.Where(p => p.Key.Item1 == e.Platform && p.Key.Item2 == sessionId))
                    match.Value.Overlay = null;
                s.Tools.Clear();
                s.CompletedTools.Clear();
                s.Permissions.Clear();
                s.Closed = false;
            }
            if (s.Closed && e.Event != HookEvent.PromptSubmitted) continue;
            s.ProjectName = e.ProjectName ?? s.ProjectName;
            s.OwnerPid = e.OwnerProcessId ?? s.OwnerPid;
            s.OwnerStart = e.OwnerProcessStartUtcTicks ?? s.OwnerStart;
            s.AgentName = e.AgentName ?? s.AgentName;
            s.LastEvent = e.Timestamp;
            switch (e.Event)
            {
                case HookEvent.PromptSubmitted:
                    s.Working = true;
                    s.Overlay = null;
                    break;
                case HookEvent.ToolStarted:
                    if (e.ToolUseId is not null && s.CompletedTools.Contains(e.ToolUseId)) break;
                    s.Working = true;
                    s.Overlay = null;
                    var synthetic = Match(s.Tools, e);
                    if (synthetic is not null && s.Tools[synthetic].ToolUseId is null &&
                        s.Tools[synthetic].Event == HookEvent.PermissionResolved)
                        s.Tools.Remove(synthetic);
                    s.Tools[e.ToolUseId ?? e.RequestId] = e;
                    break;
                case HookEvent.PermissionRequested:
                    s.Working = true;
                    s.Overlay = null;
                    s.Permissions[e.RequestId] = e;
                    break;
                case HookEvent.PermissionResolved:
                    if (e.Decision is HookDecision.None or HookDecision.Ask) break;
                    if (e.PermissionRequestId is not null) s.Permissions.Remove(e.PermissionRequestId);
                    var tool = Match(s.Tools, e);
                    if (e.Decision == HookDecision.Allow)
                    {
                        // PermissionRequest often has no tool_use_id. Reuse its
                        // matching PreToolUse instead of counting that tool twice.
                        if (tool is null) s.Tools[e.ToolUseId ?? e.RequestId] = e;
                        s.Working = true;
                    }
                    else if (tool is not null) s.Tools.Remove(tool);
                    break;
                case HookEvent.ToolCompleted:
                case HookEvent.ToolFailed:
                    if (e.ToolUseId is not null) s.CompletedTools.Add(e.ToolUseId);
                    var completed = Match(s.Tools, e);
                    if (completed is not null) s.Tools.Remove(completed);
                    var permission = Match(s.Permissions, e);
                    if (permission is not null) s.Permissions.Remove(permission);
                    s.Working = true;
                    s.Overlay = e.Event == HookEvent.ToolFailed ? AiState.Error : null;
                    break;
                case HookEvent.AgentStopped:
                    s.Tools.Clear();
                    s.Permissions.Clear();
                    s.Working = false;
                    s.Closed = true;
                    s.Overlay = AiState.Done;
                    break;
            }
        }
        dirty = false;
    }

    private static string? Match(Dictionary<string, HookRequest> candidates, HookRequest e)
    {
        if (e.ToolUseId is not null && candidates.ContainsKey(e.ToolUseId)) return e.ToolUseId;
        var exact = candidates.FirstOrDefault(p => e.ToolUseId is not null && p.Value.ToolUseId == e.ToolUseId);
        if (exact.Key is not null) return exact.Key;
        // Fingerprints correlate approval payloads without retaining tool inputs.
        var matches = candidates.Where(p =>
            (e.ToolUseId is null || p.Value.ToolUseId is null) &&
            (e.ToolFingerprint is not null
                ? p.Value.ToolFingerprint == e.ToolFingerprint
                : p.Value.ToolName == e.ToolName)).ToArray();
        return matches.Length == 0 ? null : matches[0].Key;
    }

    private sealed class Session
    {
        public readonly Dictionary<string, HookRequest> Tools = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HookRequest> Permissions = new(StringComparer.Ordinal);
        public readonly HashSet<string> CompletedTools = new(StringComparer.Ordinal);
        public bool Closed;
        public int? OwnerPid;
        public long? OwnerStart;
        public string? ProjectName;
        public string? AgentName;
        public DateTimeOffset LastEvent;
        public bool Working;
        public AiState? Overlay;

        public AiState State(DateTimeOffset now, AiSettings settings)
        {
            if (Working && now - LastEvent >= StaleAfter) return AiState.Unknown;
            if (Permissions.Count > 0) return AiState.Permission;
            if (Tools.Count > 0) return AiState.Tools;
            if (Overlay == AiState.Error && now - LastEvent < TimeSpan.FromMilliseconds(settings.ErrorOverlayMilliseconds))
                return AiState.Error;
            if (Working) return AiState.Thinking;
            if (Overlay == AiState.Done && now - LastEvent < TimeSpan.FromMilliseconds(settings.DoneOverlayMilliseconds))
                return AiState.Done;
            return AiState.Idle;
        }
    }
}
