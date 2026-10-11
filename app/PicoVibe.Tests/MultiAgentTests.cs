using PicoVibe.Core.Configuration;
using PicoVibe.Core.Ipc;
using PicoVibe.Core.Lighting;

namespace PicoVibe.Tests;

public sealed class MultiAgentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
    private static AgentSnapshot Agent(string id, AiState state = AiState.Tools, DateTimeOffset? at = null) =>
        new(id, HookPlatform.Codex, "Codex " + id, state, at ?? Now);

    [Theory]
    [InlineData(HookPlatform.Cursor)]
    [InlineData(HookPlatform.Codex)]
    [InlineData(HookPlatform.Claude)]
    public void TurnsShareOneAgentButDifferentSessionsAndChildrenDoNot(HookPlatform platform)
    {
        var tracker = new AiStateTracker();
        tracker.Apply(new() { Platform = platform, SessionId = "a", TurnId = "1", Event = HookEvent.AgentStopped, Timestamp = Now });
        tracker.Apply(new() { Platform = platform, SessionId = "a", TurnId = "2", Event = HookEvent.PromptSubmitted, Timestamp = Now.AddMilliseconds(1) });
        tracker.Apply(new() { Platform = platform, SessionId = "b", Event = HookEvent.ToolStarted, Timestamp = Now.AddMilliseconds(2) });
        tracker.Apply(new() { Platform = platform, SessionId = "a", AgentId = "child", Event = HookEvent.ToolStarted, Timestamp = Now.AddMilliseconds(3) });
        var agents = tracker.GetAgents(Now.AddSeconds(1), new AiSettings());
        Assert.Equal(3, agents.Count);
        Assert.Equal(3, agents.Select(a => a.Key).Distinct().Count());
        Assert.Contains(agents, a => a.State == AiState.Thinking);
    }

    [Fact]
    public void AssignmentIsStableAcrossStateChangesRestoreAndReverseRendering()
    {
        var layout = new AgentLampLayout();
        var a = Agent("a"); var b = Agent("b");
        var first = layout.Sync(new[] { a, b }, 12, Now, 2500);
        var restored = new AgentLampLayout();
        restored.Restore(layout.Save());
        var next = restored.Sync(new[] { b with { State = AiState.Permission }, a with { State = AiState.Thinking } }, 12, Now.AddSeconds(1), 2500);
        Assert.Equal(first.Select(l => (l.Agent.Key, l.Slot)), next.Select(l => (l.Agent.Key, l.Slot)));
        Assert.Equal(11, AgentFrameRenderer.PhysicalSlot(0, 12, true));
    }

    [Fact]
    public void OverflowDoesNotEvictWorkingOrWaitingAgents()
    {
        var layout = new AgentLampLayout();
        var twelve = Enumerable.Range(0, 12).Select(i => Agent(i.ToString("00"), i == 0 ? AiState.Permission : AiState.Tools)).ToArray();
        var initial = layout.Sync(twelve, 12, Now, 2500);
        var overflow = layout.Sync(twelve.Append(Agent("extra", at: Now.AddSeconds(1))).ToArray(), 12, Now.AddSeconds(1), 2500);
        Assert.Null(overflow.Single(l => l.Agent.Key == "extra").Slot);
        Assert.Equal(initial.Select(l => l.Slot), overflow.Take(12).Select(l => l.Slot));
    }

    [Fact]
    public void IdleOnlineSessionKeepsItsLampWhileWaitingForInput()
    {
        var layout = new AgentLampLayout();
        layout.Sync(new[] { Agent("a") }, 1, Now, 2500);
        var idle = Agent("a", AiState.Idle);
        var active = Agent("b", at: Now.AddMinutes(6));
        var result = layout.Sync(new[] { idle, active }, 1, Now.AddMinutes(6), 2500);
        Assert.Null(result.Single(l => l.Agent.Key == "b").Slot);
        Assert.Equal(0, result.Single(l => l.Agent.Key == "a").Slot);
        var again = layout.Sync(new[] { idle, active }, 1, Now.AddMinutes(6).AddSeconds(1), 2500);
        Assert.Null(again.Single(l => l.Agent.Key == "b").Slot);
    }

    [Fact]
    public void EndedSessionImmediatelyReleasesSlotWithoutInventingDone()
    {
        var layout = new AgentLampLayout();
        layout.Sync(new[] { Agent("a") }, 12, Now, 2500);
        Assert.Empty(layout.Sync(Array.Empty<AgentSnapshot>(), 12, Now.AddSeconds(1), 2500));
        Assert.Empty(layout.Sync(Array.Empty<AgentSnapshot>(), 12, Now.AddSeconds(4), 2500));
    }

    [Fact]
    public void RestoredPreferencesDoNotCreateOnlineAgentsOrReserveSlots()
    {
        var layout = new AgentLampLayout();
        layout.Restore(new[] { new AgentLampLease("old", 0, "old", HookPlatform.Cursor, Now) });
        Assert.Empty(layout.Sync(Array.Empty<AgentSnapshot>(), 12, Now, 2500));
        Assert.Equal(0, Assert.Single(layout.Sync(new[] { Agent("new") }, 12, Now, 2500)).Slot);
    }

    [Fact]
    public void OwnerExitRemovesSessionButSilenceAndUnknownLivenessDoNot()
    {
        bool? alive = true;
        var tracker = new AiStateTracker((pid, start) => alive);
        tracker.Apply(new() { SessionId = "live", Event = HookEvent.AgentStopped, Timestamp = Now,
            OwnerProcessId = 42, OwnerProcessStartUtcTicks = 123 });
        Assert.Single(tracker.GetAgents(Now.AddHours(3), new()));
        alive = null;
        Assert.Single(tracker.GetAgents(Now.AddHours(3), new()));
        alive = false;
        Assert.Empty(tracker.GetAgents(Now.AddHours(3), new()));
        Assert.Equal(AiState.Idle, tracker.GetState(Now.AddHours(3), new()));
    }

    [Fact]
    public void JournalStartupBoundaryIgnoresHistoryButRecoversNewEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), "pico-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new HookEventJournal(path);
            var start = DateTimeOffset.UtcNow;
            writer.Append(new() { SessionId = "old", Timestamp = start.AddMinutes(-1) });
            writer.Append(new() { SessionId = "new", Timestamp = start.AddMilliseconds(1) });
            Assert.Equal("new", Assert.Single(new HookEventJournal(path, start).ReadNew(start.AddSeconds(1))).SessionId);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void AnimationsAreIndependentAndUnusedLampsStayOff()
    {
        var settings = new MultiAgentSettings();
        settings.Profiles[AgentLightState.Tools] = new() { Color = new(255, 0, 0), Effect = AgentLightEffect.Blink, BrightnessPercent = 100, PeriodMilliseconds = 1000 };
        settings.Profiles[AgentLightState.Thinking] = new() { Color = new(0, 0, 255), Effect = AgentLightEffect.Breathe, BrightnessPercent = 100, PeriodMilliseconds = 2000 };
        var lamps = new[] { new AgentLamp(Agent("a"), 0), new AgentLamp(Agent("b", AiState.Thinking), 1) };
        var global = new GlobalSettings { BrightnessPercent = 100 };
        var frame = AgentFrameRenderer.Render(lamps, settings, global, Now.AddMilliseconds(750), 12);
        Assert.Equal(0, frame[0]);
        Assert.True(frame[5] > 100);
        Assert.All(frame.Skip(6), b => Assert.Equal(0, b));
        settings.Reverse = true;
        var reverse = AgentFrameRenderer.Render(lamps, settings, global, Now.AddMilliseconds(750), 12);
        Assert.Equal(frame[5], reverse[32]);
    }

    [Fact]
    public void OnlyCurrentTouchRequestUsesTouchProfileAndLocateCannotOverrideIt()
    {
        var ready = new AgentLamp(Agent("a", AiState.Permission), 0, true);
        var queued = new AgentLamp(Agent("b", AiState.Permission), 1);
        Assert.Equal(AgentLightState.Permission, AgentFrameRenderer.StateFor(ready));
        Assert.Equal(AgentLightState.NativePermission, AgentFrameRenderer.StateFor(queued));
        var lamps = new[] { ready, queued };
        var settings = new MultiAgentSettings(); var global = new GlobalSettings();
        Assert.Equal(AgentFrameRenderer.Render(lamps, settings, global, Now, 12),
            AgentFrameRenderer.Render(lamps, settings, global, Now, 12, "b"));
        Assert.Equal(AgentLightState.Permission, AgentFrameRenderer.StateFor(ready with { Agent = Agent("a", AiState.Thinking) }));
    }

    [Fact]
    public void MultiAgentConfigMigratesWithoutChangingOverallProfiles()
    {
        var config = AppConfig.CreateDefault();
        var original = config.Profiles["ai-thinking"].Primary;
        config.Ai.MultiAgent.Profiles[AgentLightState.Thinking].Color = new(255, 0, 0);
        var normalized = AppConfigDefaults.Normalize(config);
        Assert.Equal(original, normalized.Profiles["ai-thinking"].Primary);
        var clone = normalized.Ai.MultiAgent.Clone();
        clone.Profiles[AgentLightState.Thinking].Color = new(0, 255, 0);
        Assert.Equal(new RgbColor(255, 0, 0), normalized.Ai.MultiAgent.Profiles[AgentLightState.Thinking].Color);
    }
}
