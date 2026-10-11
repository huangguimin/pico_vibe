using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Ipc;
using PicoVibe.Hook;

namespace PicoVibe.Hook.Tests;

public sealed class HookLifecycleTests
{
    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("PermissionRequest")]
    [InlineData("Stop")]
    public async Task CursorImportedClaudeHookDoesNotPublishOrArmApproval(string eventName)
    {
        var (requests, output) = await RunAsync("claude-hook", eventName,
            """{"session_id":"cursor-session","cursor_version":"2026.09.10","hook_event_name":"sessionStart"}""",
            AppConfig.CreateDefault(), 0, brokerAvailable: false);
        Assert.Empty(requests);
        Assert.Equal("{}", output.Trim());
    }

    private const string Input = """
        {"session_id":"session","conversation_id":"session","turn_id":"turn","generation_id":"turn",
         "tool_use_id":"tool","tool_name":"Write","tool_input":{"command":"test"},"sandbox":false}
        """;

    [Theory]
    [InlineData("hook", "postToolUse", HookEvent.ToolCompleted)]
    [InlineData("hook", "postToolUseFailure", HookEvent.ToolFailed)]
    [InlineData("codex-hook", "PostToolUse", HookEvent.ToolCompleted)]
    [InlineData("claude-hook", "PostToolUse", HookEvent.ToolCompleted)]
    [InlineData("claude-hook", "PostToolUseFailure", HookEvent.ToolFailed)]
    public async Task ClientLifecycleArrivesAtBrokerWithIdentity(string command, string eventName, HookEvent expected)
    {
        var (requests, _) = await RunAsync(command, eventName, Input, AppConfig.CreateDefault(), 1);
        var request = Assert.Single(requests);
        Assert.Equal(expected, request.Event);
        Assert.Equal("session", request.SessionId);
        Assert.Equal("turn", request.TurnId);
        Assert.Equal("tool", request.ToolUseId);
        Assert.NotNull(request.ToolFingerprint);
        Assert.DoesNotContain("\"command\"", HookJson.Serialize(request), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hook", "beforeShellExecution")]
    [InlineData("codex-hook", "PermissionRequest")]
    [InlineData("claude-hook", "PermissionRequest")]
    public async Task ApprovalPublishesResolutionAndExecutionEvenWithoutEarlierToolEvent(string command, string eventName)
    {
        var config = AppConfig.CreateDefault();
        config.Ai.CursorShellGate = CursorShellGate.NonSandbox;
        var (requests, output) = await RunAsync(command, eventName, Input, config, 2);
        Assert.Equal(HookEvent.PermissionRequested, requests[0].Event);
        Assert.Equal(HookEvent.PermissionResolved, requests[1].Event);
        Assert.Equal(requests[0].RequestId, requests[1].PermissionRequestId);
        Assert.Equal(HookDecision.Allow, requests[1].Decision);
        Assert.Contains("allow", output, StringComparison.Ordinal);
        var tracker = new AiStateTracker();
        foreach (var request in requests) tracker.Apply(request);
        Assert.Equal(AiState.Tools, tracker.GetState(DateTimeOffset.UtcNow, AppConfig.CreateDefault().Ai));
    }

    [Theory]
    [InlineData("beforeShellExecution")]
    [InlineData("beforeMCPExecution")]
    public async Task DefaultCursorGateDoesNotWaitPublishPermissionOrOverrideNativeDecision(string eventName)
    {
        var (requests, output) = await RunAsync("hook", eventName, Input,
            AppConfig.CreateDefault(), 0, brokerAvailable: false);
        Assert.Empty(requests);
        Assert.Equal("{}", output.Trim());
    }

    [Theory]
    [InlineData("codex-hook")]
    [InlineData("claude-hook")]
    public async Task DisabledTouchStillObservesNativeApproval(string command)
    {
        var config = AppConfig.CreateDefault();
        config.Ai.CodexTouchApproval = false;
        var (requests, output) = await RunAsync(command, "PermissionRequest", Input, config, 2);
        Assert.True(requests[0].ObserveOnly);
        Assert.Equal(HookDecision.None, requests[1].Decision);
        Assert.Equal("{}", output.Trim());
        var tracker = new AiStateTracker();
        foreach (var request in requests) tracker.Apply(request);
        Assert.Equal(AiState.Permission, tracker.GetState(DateTimeOffset.UtcNow, config.Ai));
    }

    [Theory]
    [InlineData("hook", "preToolUse")]
    [InlineData("codex-hook", "PreToolUse")]
    [InlineData("claude-hook", "PreToolUse")]
    public async Task MissingBrokerLeavesRecoverableDisplayEvent(string command, string eventName)
    {
        var (recovered, _) = await RunAsync(command, eventName, Input, AppConfig.CreateDefault(), 1, brokerAvailable: false);
        var request = Assert.Single(recovered);
        Assert.Equal(HookEvent.ToolStarted, request.Event);
        Assert.True(request.ObserveOnly);
    }

    [Fact]
    public void CursorMcpJsonStringAndObjectShareACorrelationFingerprint()
    {
        using var pre = JsonDocument.Parse("""{"tool_input":{"x":1,"y":2}}""");
        using var gate = JsonDocument.Parse("""{"tool_input":"{\"y\":2,\"x\":1}"}""");
        var a = Program.CreateRequest(HookPlatform.Cursor, HookEvent.ToolStarted, pre.RootElement, AiState.Tools);
        var b = Program.CreateRequest(HookPlatform.Cursor, HookEvent.PermissionRequested, gate.RootElement, AiState.Permission);
        Assert.Equal(a.ToolFingerprint, b.ToolFingerprint);
    }

    [Theory]
    [InlineData("{\"tool_response\":{\"exit_code\":1}}", true)]
    [InlineData("{\"tool_response\":{\"isError\":true}}", true)]
    [InlineData("{\"tool_output\":\"{\\\"exitCode\\\":2}\"}", true)]
    [InlineData("{\"tool_response\":{\"exit_code\":0}}", false)]
    [InlineData("{\"tool_response\":{\"exit_code\":\"unknown\"}}", false)]
    [InlineData("{\"tool_response\":\"example mentions error but succeeded\"}", false)]
    public void FailureDetectionUsesStructuredEvidence(string input, bool expected)
    {
        using var json = JsonDocument.Parse(input);
        Assert.Equal(expected, Program.HasToolFailure(json.RootElement));
    }

    private static async Task<(List<HookRequest>, string)> RunAsync(
        string command, string eventName, string input, AppConfig config, int expectedRequests, bool brokerAvailable = true)
    {
        var home = Path.Combine(Path.GetTempPath(), "PicoVibe.Hook.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var previousHome = Environment.GetEnvironmentVariable(HookInstaller.TestHomeEnvironmentVariable);
        var previousIn = Console.In;
        var previousOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable(HookInstaller.TestHomeEnvironmentVariable, home);
            await new ConfigStore(Path.Combine(home, "config.json")).SaveAsync(config);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var captured = new List<HookRequest>();
            var pipeName = Program.TestPipeName();
            var broker = Task.Run(async () =>
            {
                if (!brokerAvailable) return;
                for (var i = 0; i < expectedRequests; i++)
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(cancellation.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    var request = HookJson.Deserialize<HookRequest>((await reader.ReadLineAsync(cancellation.Token))!)!;
                    captured.Add(request);
                    await writer.WriteLineAsync(HookJson.Serialize(new HookResponse
                    {
                        RequestId = request.RequestId,
                        Decision = request.ObserveOnly ? HookDecision.None : HookDecision.Allow,
                    }).AsMemory(), cancellation.Token);
                }
            });
            using var stdin = new StringReader(input);
            using var stdout = new StringWriter();
            Console.SetIn(stdin);
            Console.SetOut(stdout);
            Assert.Equal(0, await Program.Main([command, "--event", eventName]));
            await broker;
            var saved = new HookEventJournal(Path.Combine(home, "hook-events")).ReadNew(DateTimeOffset.UtcNow);
            Assert.Equal(expectedRequests, saved.Count);
            return (brokerAvailable ? captured : saved.ToList(), stdout.ToString());
        }
        finally
        {
            Console.SetIn(previousIn);
            Console.SetOut(previousOut);
            Environment.SetEnvironmentVariable(HookInstaller.TestHomeEnvironmentVariable, previousHome);
            Directory.Delete(home, true);
        }
    }
}
