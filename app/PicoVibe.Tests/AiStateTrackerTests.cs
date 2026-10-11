using PicoVibe.Core.Configuration;
using PicoVibe.Core.Ipc;

namespace PicoVibe.Tests;

public sealed class AiStateTrackerTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-12T00:00:00Z");
    private readonly AiSettings settings = AppConfig.CreateDefault().Ai;
    private static HookRequest E(HookEvent kind, int time, string session = "a", string? tool = null,
        HookPlatform platform = HookPlatform.Codex, string? turn = null) => new()
    {
        Event = kind, Timestamp = Epoch.AddMilliseconds(time), SessionId = session,
        ToolUseId = tool, ToolName = "Bash", ToolFingerprint = tool ?? "command",
        Platform = platform, TurnId = turn,
    };
    private AiState State(AiStateTracker tracker, int time = 100) => tracker.GetState(Epoch.AddMilliseconds(time), settings);

    [Theory]
    [InlineData(HookPlatform.Cursor)]
    [InlineData(HookPlatform.Codex)]
    [InlineData(HookPlatform.Claude)]
    public void OneSessionStoppingDoesNotHideAnotherSessionsTools(HookPlatform platform)
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.ToolStarted, 1, tool: "t", platform: platform));
        tracker.Apply(E(HookEvent.AgentStopped, 2, session: "b", platform: platform));
        Assert.Equal(AiState.Tools, State(tracker));
        tracker.Apply(E(HookEvent.SessionEnded, 3, session: "b", platform: platform));
        Assert.Equal(AiState.Tools, State(tracker));
    }

    [Theory]
    [InlineData(HookPlatform.Cursor)]
    [InlineData(HookPlatform.Codex)]
    [InlineData(HookPlatform.Claude)]
    public void ParallelToolsAndDuplicateCompletionRemainConsistent(HookPlatform platform)
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.ToolStarted, 1, tool: "one", platform: platform) with { ToolFingerprint = "same" });
        tracker.Apply(E(HookEvent.ToolStarted, 2, tool: "two", platform: platform) with { ToolFingerprint = "same" });
        var completed = E(HookEvent.ToolCompleted, 3, tool: "one", platform: platform) with { ToolFingerprint = "same" };
        tracker.Apply(completed);
        tracker.Apply(completed);
        tracker.Apply(completed with { RequestId = "another-notification" });
        Assert.Equal(AiState.Tools, State(tracker));
        tracker.Apply(E(HookEvent.ToolCompleted, 4, tool: "two", platform: platform));
        Assert.Equal(AiState.Thinking, State(tracker));
    }

    [Theory]
    [InlineData(HookEvent.AgentStopped)]
    [InlineData(HookEvent.ToolFailed)]
    public void NewWorkClearsOldOverlay(HookEvent overlay)
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(overlay, 1));
        Assert.Contains(State(tracker), new[] { AiState.Done, AiState.Error });
        tracker.Apply(E(HookEvent.PromptSubmitted, 2));
        Assert.Equal(AiState.Thinking, State(tracker));
        tracker.Apply(E(HookEvent.ToolStarted, 3, tool: "tool"));
        Assert.Equal(AiState.Tools, State(tracker));
    }

    [Fact]
    public void LateDeliveryAndRestartReplayGiveTheSameState()
    {
        var start = E(HookEvent.ToolStarted, 1, tool: "one");
        var end = E(HookEvent.ToolCompleted, 2, tool: "one");
        var tracker = new AiStateTracker();
        tracker.Apply(end);
        tracker.Apply(start);
        Assert.Equal(AiState.Thinking, State(tracker));
        tracker.Apply(E(HookEvent.AgentStopped, 3));
        tracker.Apply(E(HookEvent.ToolStarted, 0, tool: "late"));
        Assert.Equal(AiState.Done, State(tracker));
    }

    [Fact]
    public void SessionStartForCompactionDoesNotResetWork()
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.ToolStarted, 1, tool: "one"));
        tracker.Apply(E(HookEvent.SessionStarted, 2));
        Assert.Equal(AiState.Tools, State(tracker));
    }

    [Fact]
    public void OldTurnStoppingDoesNotHideNewTurn()
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.PromptSubmitted, 1, turn: "old"));
        tracker.Apply(E(HookEvent.PromptSubmitted, 2, turn: "new"));
        tracker.Apply(E(HookEvent.AgentStopped, 3, turn: "old"));
        Assert.Equal(AiState.Thinking, State(tracker));
        tracker.Apply(E(HookEvent.SessionEnded, 4));
        Assert.Equal(AiState.Idle, State(tracker));
    }

    [Theory]
    [InlineData(HookPlatform.Cursor)]
    [InlineData(HookPlatform.Codex)]
    [InlineData(HookPlatform.Claude)]
    public void PermissionAllowReusesExistingToolThenCompletionReturnsToThinking(HookPlatform platform)
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.ToolStarted, 1, tool: "one", platform: platform) with { ToolFingerprint = "command" });
        var ask = E(HookEvent.PermissionRequested, 2, platform: platform);
        tracker.Apply(ask);
        Assert.Equal(AiState.Permission, State(tracker));
        tracker.Apply(ask with { RequestId = "resolution", Event = HookEvent.PermissionResolved,
            PermissionRequestId = ask.RequestId, Timestamp = Epoch.AddMilliseconds(3), Decision = HookDecision.Allow });
        Assert.Equal(AiState.Tools, State(tracker));
        tracker.Apply(E(HookEvent.ToolCompleted, 4, tool: "one", platform: platform) with { ToolFingerprint = "command" });
        Assert.Equal(AiState.Thinking, State(tracker));
    }

    [Fact]
    public void NativeFallbackRetainsPermissionUntilMatchingToolFinishes()
    {
        var tracker = new AiStateTracker();
        var ask = E(HookEvent.PermissionRequested, 1);
        tracker.Apply(ask);
        tracker.Apply(ask with { RequestId = "fallback", Event = HookEvent.PermissionResolved,
            PermissionRequestId = ask.RequestId, Timestamp = Epoch.AddMilliseconds(2), Decision = HookDecision.None });
        tracker.Apply(E(HookEvent.ToolCompleted, 3, tool: "other"));
        Assert.Equal(AiState.Permission, State(tracker));
        tracker.Apply(E(HookEvent.ToolCompleted, 4, tool: "one") with { ToolFingerprint = "command" });
        Assert.Equal(AiState.Thinking, State(tracker));
    }

    [Fact]
    public void LateToolEventsCannotReopenEndedSession()
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.ToolStarted, 1, tool: "one"));
        tracker.Apply(E(HookEvent.SessionEnded, 2));
        tracker.Apply(E(HookEvent.ToolCompleted, 3, tool: "one"));
        Assert.Equal(AiState.Idle, State(tracker));
        tracker.Apply(E(HookEvent.PromptSubmitted, 4));
        Assert.Equal(AiState.Thinking, State(tracker));
    }

    [Fact]
    public void MissingEventsBecomeUnknownAndFreshEventsRecover()
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.ToolStarted, 1, tool: "one"));
        Assert.Equal(AiState.Unknown, State(tracker, 600002));
        tracker.Apply(E(HookEvent.ToolCompleted, 600003, tool: "one"));
        Assert.Equal(AiState.Thinking, State(tracker, 600004));
    }

    [Fact]
    public void ApprovalBeforePreToolUseDoesNotDoubleCountCursorTool()
    {
        var tracker = new AiStateTracker();
        var ask = E(HookEvent.PermissionRequested, 1, platform: HookPlatform.Cursor);
        tracker.Apply(ask);
        tracker.Apply(ask with { RequestId = "allow", Event = HookEvent.PermissionResolved,
            PermissionRequestId = ask.RequestId, Decision = HookDecision.Allow, Timestamp = Epoch.AddMilliseconds(2) });
        tracker.Apply(E(HookEvent.ToolStarted, 3, tool: "one", platform: HookPlatform.Cursor) with { ToolFingerprint = "command" });
        tracker.Apply(E(HookEvent.ToolCompleted, 4, tool: "one", platform: HookPlatform.Cursor) with { ToolFingerprint = "command" });
        Assert.Equal(AiState.Thinking, State(tracker));
    }

    [Fact]
    public void ClaudeSubagentStoppingDoesNotCloseParentOrOtherSubagents()
    {
        var tracker = new AiStateTracker();
        tracker.Apply(E(HookEvent.PromptSubmitted, 1, platform: HookPlatform.Claude));
        tracker.Apply(E(HookEvent.ToolStarted, 2, tool: "one", platform: HookPlatform.Claude) with { AgentId = "child" });
        tracker.Apply(E(HookEvent.AgentStopped, 3, platform: HookPlatform.Claude));
        Assert.Equal(AiState.Tools, State(tracker));
        tracker.Apply(E(HookEvent.AgentStopped, 4, platform: HookPlatform.Claude) with { AgentId = "child" });
        Assert.Equal(AiState.Done, State(tracker));
    }

    [Fact]
    public void JournalReplaysObservedPermissionWithoutTouchCapability()
    {
        using var directory = new TestDirectory();
        var journal = new HookEventJournal(directory.Path);
        journal.Append(E(HookEvent.PermissionRequested, 1));
        var restored = Assert.Single(new HookEventJournal(directory.Path).ReadNew(DateTimeOffset.UtcNow));
        Assert.True(restored.ObserveOnly);
        var tracker = new AiStateTracker();
        tracker.Apply(restored);
        Assert.Equal(AiState.Permission, State(tracker));
    }
}
