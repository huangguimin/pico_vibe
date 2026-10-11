using System.Text.Json;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Ipc;
using PicoVibe.Hook;

namespace PicoVibe.Hook.Tests;

public sealed class HookOutputTests
{
    [Fact]
    public void CursorOnlyArmsTouchAtConfiguredApprovalBoundaries()
    {
        var config = AppConfig.CreateDefault();
        config.Ai.CursorShellGate = CursorShellGate.NonSandbox;
        config.Ai.CursorMcpGate = CursorMcpGate.Mutating;
        Assert.False(Program.ShouldRequestCursorPermission(
            config,
            "beforeShellExecution",
            Json("{\"sandbox\":true}")));
        Assert.True(Program.ShouldRequestCursorPermission(
            config,
            "beforeShellExecution",
            Json("{\"sandbox\":false}")));
        Assert.False(Program.ShouldRequestCursorPermission(
            config,
            "beforeMCPExecution",
            Json("{\"tool_name\":\"read_file\"}")));
        Assert.True(Program.ShouldRequestCursorPermission(
            config,
            "beforeMCPExecution",
            Json("{\"tool_name\":\"deploy_app\"}")));
        Assert.False(Program.ShouldRequestCursorPermission(
            config,
            "preToolUse",
            Json("{\"tool_name\":\"Write\"}")));

        config.Ai.GateFileTools = true;
        Assert.True(Program.ShouldRequestCursorPermission(
            config,
            "preToolUse",
            Json("{\"tool_name\":\"Write\"}")));
        config.Ai.Enabled = false;
        Assert.False(Program.ShouldRequestCursorPermission(
            config,
            "beforeShellExecution",
            Json("{\"sandbox\":false}")));
    }

    [Fact]
    public async Task MissingTrayAppUsesShortConnectDeadline()
    {
        var request = new HookRequest
        {
            Platform = HookPlatform.Codex,
            Event = HookEvent.PermissionRequested,
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await new HookPipeClient($"pvm-{Guid.NewGuid():N}"[..20])
            .SendAsync(request, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Assert.Null(response);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Missing-server fallback took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void CodexPermissionRequestUsesNativeDecisionShape()
    {
        AssertNativePermissionDecisionShape(Program.BuildCodexDecision);
    }

    [Fact]
    public void ClaudePermissionRequestUsesNativeDecisionShape()
    {
        AssertNativePermissionDecisionShape(Program.BuildClaudeDecision);
    }

    [Fact]
    public async Task ClaudeNotificationNeverArmsTouchPermission()
    {
        var previousIn = Console.In;
        var previousOut = Console.Out;
        using var input = new StringReader(
            "{\"notification_type\":\"permission_prompt\",\"tool_name\":\"Bash\"}");
        using var output = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);

            var exitCode = await Program.Main(
                ["claude-hook", "--event", "Notification"]);

            Assert.Equal(0, exitCode);
            Assert.Equal("{}", output.ToString().Trim());
        }
        finally
        {
            Console.SetIn(previousIn);
            Console.SetOut(previousOut);
        }
    }

    private static void AssertNativePermissionDecisionShape(
        Func<HookResponse?, Dictionary<string, object?>> build)
    {
        var allow = build(Response(HookDecision.Allow, "Touched allow."));
        using var allowDocument = JsonDocument.Parse(JsonSerializer.Serialize(allow));
        var output = allowDocument.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PermissionRequest", output.GetProperty("hookEventName").GetString());
        var decision = output.GetProperty("decision");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        Assert.False(decision.TryGetProperty("message", out _));

        var deny = build(Response(HookDecision.Deny, "Touched deny."));
        using var denyDocument = JsonDocument.Parse(JsonSerializer.Serialize(deny));
        var denyDecision = denyDocument.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("decision");
        Assert.Equal("deny", denyDecision.GetProperty("behavior").GetString());
        Assert.Equal("Touched deny.", denyDecision.GetProperty("message").GetString());

        Assert.Empty(build(Response(HookDecision.None)));
        Assert.Empty(build(null));

        var serialized = JsonSerializer.Serialize(new[] { allow, deny });
        Assert.DoesNotContain("SendKeys", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enter", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Escape", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CursorUngatedHookDoesNotGrantPermission()
    {
        Assert.Empty(Program.BuildCursorUngatedOutput());
    }

    [Fact]
    public void CursorGatedHookFailsSafeClosedWithoutBrokerDecision()
    {
        var noDevice = Program.BuildCursorDecision(Response(HookDecision.None, "No device."));
        Assert.Equal("deny", noDevice["permission"]);
        Assert.Equal(
            "Pico Vibe touch approval was unavailable; Cursor blocked the gated operation.",
            noDevice["user_message"]);
        Assert.Equal(noDevice["user_message"], noDevice["agent_message"]);

        var timedOut = Program.BuildCursorDecision(Response(HookDecision.Ask, "Touch timed out."));
        Assert.Equal("deny", timedOut["permission"]);
        Assert.Equal(noDevice["agent_message"], timedOut["agent_message"]);

        var missingBroker = Program.BuildCursorDecision(null);
        Assert.Equal("deny", missingBroker["permission"]);
        Assert.Equal(noDevice["agent_message"], missingBroker["agent_message"]);

        var deniedByTouch = Program.BuildCursorDecision(Response(HookDecision.Deny));
        Assert.Equal("deny", deniedByTouch["permission"]);
        Assert.Equal("Pico Vibe denied the gated operation.", deniedByTouch["agent_message"]);

        var allow = Program.BuildCursorDecision(Response(HookDecision.Allow));
        Assert.Equal("allow", allow["permission"]);
        Assert.False(allow.ContainsKey("user_message"));
    }

    private static HookResponse Response(HookDecision decision, string? message = null) => new()
    {
        RequestId = "test",
        Decision = decision,
        Message = message,
    };

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
