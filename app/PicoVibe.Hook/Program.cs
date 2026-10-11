using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Ipc;

namespace PicoVibe.Hook;

internal static class Program
{
    private static readonly HashSet<string> MutatingMcpHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "write", "update", "edit", "delete", "remove", "send", "post",
        "put", "publish", "deploy", "install", "execute", "run", "apply", "merge",
        "commit", "push", "move", "rename",
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return 2;
            }

            return args[0].ToLowerInvariant() switch
            {
                "hook" => await RunCursorHookAsync(args[1..]).ConfigureAwait(false),
                "codex-hook" => await RunCodexHookAsync(args[1..]).ConfigureAwait(false),
                "claude-hook" => await RunClaudeHookAsync(args[1..]).ConfigureAwait(false),
                "control" => await RunControlAsync(args[1..]).ConfigureAwait(false),
                "install-hooks" => HookInstaller.Install(GetOption(args, "--skill-source")),
                "uninstall-hooks" => HookInstaller.Uninstall(),
                _ => 2,
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Pico Vibe hook error: {exception.Message}");
            try
            {
                var testHome = Environment.GetEnvironmentVariable(
                    HookInstaller.TestHomeEnvironmentVariable);
                var logRoot = !string.IsNullOrWhiteSpace(testHome) &&
                    Path.IsPathFullyQualified(testHome)
                    ? testHome
                    : Path.GetTempPath();
                Directory.CreateDirectory(logRoot);
                var logPath = Path.Combine(logRoot, "pico-vibe-hook.log");
                await File.AppendAllTextAsync(
                    logPath,
                    $"{DateTimeOffset.Now:o} {exception}\n").ConfigureAwait(false);
            }
            catch
            {
            }
            return 1;
        }
    }

    private static async Task<int> RunCursorHookAsync(string[] args)
    {
        var statusName = args.FirstOrDefault() ?? "tools";
        var eventName = GetOption(args, "--event") ?? string.Empty;
        var payload = await ReadStdinJsonAsync().ConfigureAwait(false);
        var config = await LoadConfigAsync().ConfigureAwait(false);

        if (eventName is "beforeShellExecution" or "beforeMCPExecution" or "preToolUse")
        {
            if (ShouldRequestCursorPermission(config, eventName, payload))
            {
                var response = await RequestPermissionAsync(
                    HookPlatform.Cursor,
                    payload,
                    config).ConfigureAwait(false);
                return EmitCursorDecision(response);
            }

            if (eventName != "preToolUse") return Emit(BuildCursorUngatedOutput());

            await PublishStatusAsync(
                HookPlatform.Cursor,
                HookEvent.ToolStarted,
                AiState.Tools,
                payload).ConfigureAwait(false);
            return Emit(BuildCursorUngatedOutput());
        }

        var (hookEvent, state) = eventName switch
        {
            "sessionStart" => (HookEvent.SessionStarted, AiState.Idle),
            "beforeSubmitPrompt" => (HookEvent.PromptSubmitted, AiState.Thinking),
            "postToolUse" => (HasToolFailure(payload) ? HookEvent.ToolFailed : HookEvent.ToolCompleted, AiState.Thinking),
            "postToolUseFailure" => (HookEvent.ToolFailed, AiState.Error),
            "stop" => (HookEvent.AgentStopped, AiState.Done),
            "sessionEnd" => (HookEvent.SessionEnded, AiState.Idle),
            _ => (HookEvent.ToolStarted, ParseState(statusName)),
        };
        await PublishStatusAsync(HookPlatform.Cursor, hookEvent, state, payload)
            .ConfigureAwait(false);
        return eventName == "beforeSubmitPrompt"
            ? Emit(new Dictionary<string, object?> { ["continue"] = true })
            : Emit(new Dictionary<string, object?>());
    }

    // Claude Code 2.x exposes a native, blocking PermissionRequest hook.  Keep
    // that boundary synchronous so the physical gesture is returned to Claude
    // as its native allow/deny decision.  Notifications are deliberately not
    // treated as permission requests: they include idle/auth messages and
    // cannot return an approval decision.
    private static async Task<int> RunClaudeHookAsync(string[] args)
    {
        var eventName = GetOption(args, "--event") ?? string.Empty;
        var payload = await ReadStdinJsonAsync().ConfigureAwait(false);
        // Cursor also loads ~/.claude/settings.json. Its native Pico Vibe
        // hook owns those events; do not create a second Claude lamp or gate.
        // Use the event payload, not inherited shell environment variables.
        if (IsCursorCompatibilityPayload(payload))
            return Emit(new Dictionary<string, object?>());
        var config = await LoadConfigAsync().ConfigureAwait(false);
        if (eventName == "PermissionRequest")
        {
            if (!config.Ai.Enabled)
            {
                return Emit(new Dictionary<string, object?>());
            }

            var response = await RequestPermissionAsync(HookPlatform.Claude, payload, config)
                .ConfigureAwait(false);
            return EmitNativePermissionDecision(response);
        }

        if (eventName == "Notification")
        {
            return Emit(new Dictionary<string, object?>());
        }

        var mapped = eventName switch
        {
            "SessionStart" => (HookEvent.SessionStarted, AiState.Idle),
            "SessionEnd" => (HookEvent.SessionEnded, AiState.Idle),
            "UserPromptSubmit" => (HookEvent.PromptSubmitted, AiState.Thinking),
            "PreToolUse" => (HookEvent.ToolStarted, AiState.Tools),
            "PostToolUse" => (HasToolFailure(payload) ? HookEvent.ToolFailed : HookEvent.ToolCompleted, AiState.Thinking),
            "PostToolUseFailure" => (HookEvent.ToolFailed, AiState.Error),
            "SubagentStart" => (HookEvent.PromptSubmitted, AiState.Thinking),
            "Stop" or "SubagentStop" => (HookEvent.AgentStopped, AiState.Done),
            _ => (HookEvent.ToolStarted, AiState.Tools),
        };
        await PublishStatusAsync(HookPlatform.Claude, mapped.Item1, mapped.Item2, payload)
            .ConfigureAwait(false);
        return Emit(new Dictionary<string, object?>());
    }

    internal static bool IsCursorCompatibilityPayload(JsonElement payload) =>
        !string.IsNullOrWhiteSpace(GetString(payload, "cursor_version"));

    private static async Task<int> RunCodexHookAsync(string[] args)
    {
        var eventName = GetOption(args, "--event") ?? string.Empty;
        var payload = await ReadStdinJsonAsync().ConfigureAwait(false);
        var config = await LoadConfigAsync().ConfigureAwait(false);
        if (eventName == "PermissionRequest")
        {
            if (!config.Ai.Enabled)
            {
                return Emit(new Dictionary<string, object?>());
            }

            var response = await RequestPermissionAsync(HookPlatform.Codex, payload, config)
                .ConfigureAwait(false);
            return EmitNativePermissionDecision(response);
        }

        var mapped = eventName switch
        {
            "SessionStart" => (HookEvent.SessionStarted, AiState.Idle),
            "SessionEnd" => (HookEvent.SessionEnded, AiState.Idle),
            "UserPromptSubmit" => (HookEvent.PromptSubmitted, AiState.Thinking),
            "PreToolUse" => (HookEvent.ToolStarted, AiState.Tools),
            "PostToolUse" => (HasToolFailure(payload) ? HookEvent.ToolFailed : HookEvent.ToolCompleted, AiState.Thinking),
            "Stop" => (HookEvent.AgentStopped, AiState.Done),
            _ => (HookEvent.ToolStarted, AiState.Tools),
        };
        await PublishStatusAsync(HookPlatform.Codex, mapped.Item1, mapped.Item2, payload)
            .ConfigureAwait(false);
        return Emit(new Dictionary<string, object?>());
    }

    internal static bool ShouldRequestCursorPermission(
        AppConfig config,
        string eventName,
        JsonElement payload)
    {
        if (!config.Ai.Enabled)
        {
            return false;
        }

        if (eventName == "beforeShellExecution")
        {
            return config.Ai.CursorShellGate switch
            {
                CursorShellGate.All => true,
                CursorShellGate.NonSandbox =>
                    payload.TryGetProperty("sandbox", out var sandbox) &&
                    sandbox.ValueKind == JsonValueKind.False,
                _ => false,
            };
        }

        if (eventName == "beforeMCPExecution")
        {
            if (config.Ai.CursorMcpGate == CursorMcpGate.All)
            {
                return true;
            }
            if (config.Ai.CursorMcpGate == CursorMcpGate.Never)
            {
                return false;
            }
            var name = GetString(payload, "tool_name").ToLowerInvariant();
            return MutatingMcpHints.Any(name.Contains);
        }

        if (eventName == "preToolUse" && config.Ai.GateFileTools)
        {
            var name = GetString(payload, "tool_name");
            return name is "Write" or "Delete" or "StrReplace" or "EditNotebook";
        }
        return false;
    }

    private static async Task<HookResponse?> RequestPermissionAsync(
        HookPlatform platform,
        JsonElement payload,
        AppConfig config)
    {
        var request = CreateRequest(platform, HookEvent.PermissionRequested, payload, AiState.Permission) with
        {
            ObserveOnly = !config.Touch.Enabled ||
                (platform != HookPlatform.Cursor && !config.Ai.CodexTouchApproval),
        };
        RecordEvent(request);
        HookResponse? response;
        if (request.ObserveOnly)
        {
            await SendToAppAsync(request, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            response = HookResponse.NoDecision(request, "Touch approval is disabled.");
        }
        else
        {
            var timeout = TimeSpan.FromSeconds(config.Ai.ApprovalTimeoutSeconds + 3);
            response = await SendToAppAsync(request, timeout).ConfigureAwait(false)
                ?? HookResponse.NoDecision(request, "Pico Vibe app is unavailable.");
        }
        var decision = response.Decision;
        if (platform == HookPlatform.Cursor && decision is HookDecision.None or HookDecision.Ask)
            decision = HookDecision.Deny;
        var resolved = request with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            Event = HookEvent.PermissionResolved,
            PermissionRequestId = request.RequestId,
            Decision = decision,
            ObserveOnly = true,
        };
        RecordEvent(resolved);
        await SendToAppAsync(resolved, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return response;
    }

    private static async Task PublishStatusAsync(
        HookPlatform platform,
        HookEvent hookEvent,
        AiState state,
        JsonElement payload)
    {
        var request = CreateRequest(platform, hookEvent, payload, state);
        RecordEvent(request);
        var response = await SendToAppAsync(request, TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        if (response is not null)
        {
            return;
        }
    }

    internal static HookRequest CreateRequest(
        HookPlatform platform,
        HookEvent hookEvent,
        JsonElement payload,
        AiState state)
    {
        var owner = AgentProcessIdentity.Capture(platform);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["state"] = state.ToString(),
        };
        var toolName = GetString(payload, "tool_name");
        bool? sandboxed = payload.TryGetProperty("sandbox", out var sandbox) &&
            sandbox.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? sandbox.GetBoolean()
            : null;
        return new HookRequest
        {
            Platform = platform,
            Event = hookEvent,
            ProjectName = ProjectName(payload),
            AgentName = FirstString(payload, "agent_type", "subagent_type"),
            SessionId = FirstString(payload, "session_id", "conversation_id", "thread_id"),
            OwnerProcessId = owner.Pid,
            OwnerProcessStartUtcTicks = owner.Start,
            AgentId = platform == HookPlatform.Claude ? FirstString(payload, "agent_id") : null,
            TurnId = FirstString(payload, "turn_id", "generation_id"),
            ToolUseId = FirstString(payload, "tool_use_id", "tool_call_id", "call_id"),
            ToolFingerprint = ToolFingerprint(payload),
            ToolName = string.IsNullOrWhiteSpace(toolName) ? null : toolName,
            Sandboxed = sandboxed,
            Metadata = metadata,
        };
    }

    private static string? ProjectName(JsonElement payload)
    {
        var path = FirstString(payload, "cwd");
        if (path is null && payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("workspace_roots", out var roots) && roots.ValueKind == JsonValueKind.Array)
            path = roots.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String
                ? roots[0].GetString() : null;
        return path?.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    private static string? FirstString(JsonElement payload, params string[] names) =>
        names.Select(name => GetString(payload, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? ToolFingerprint(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        string? input = null;
        if (payload.TryGetProperty("tool_input", out var toolInput))
        {
            if (toolInput.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(toolInput.GetString()!);
                    toolInput = parsed.RootElement.Clone();
                }
                catch (JsonException) { }
            }
            // Shell approval inputs can add description/cwd; the command remains stable.
            input = toolInput.ValueKind == JsonValueKind.Object && toolInput.TryGetProperty("command", out var command)
                ? command.GetRawText() : CanonicalJson(toolInput);
        }
        else if (payload.TryGetProperty("command", out var command)) input = command.GetRawText();
        return input is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string CanonicalJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalJson(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(CanonicalJson)) + "]",
        _ => value.GetRawText(),
    };

    internal static bool HasToolFailure(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in new[] { "tool_response", "tool_output" })
        {
            if (!payload.TryGetProperty(field, out var result)) continue;
            if (result.ValueKind == JsonValueKind.String)
            {
                try { using var parsed = JsonDocument.Parse(result.GetString()!); if (ResultFailed(parsed.RootElement)) return true; }
                catch (JsonException) { }
            }
            else if (ResultFailed(result)) return true;
        }
        return false;
    }

    private static bool ResultFailed(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in new[] { "isError", "is_error" })
            if (value.TryGetProperty(field, out var flag) && flag.ValueKind == JsonValueKind.True) return true;
        foreach (var field in new[] { "exit_code", "exitCode" })
            if (value.TryGetProperty(field, out var code) && code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var number) && number != 0) return true;
        return false;
    }

    private static void RecordEvent(HookRequest request)
    {
        try
        {
            var testHome = Environment.GetEnvironmentVariable(HookInstaller.TestHomeEnvironmentVariable);
            var directory = string.IsNullOrWhiteSpace(testHome) ? HookEventJournal.DefaultDirectory
                : Path.Combine(testHome, "hook-events");
            new HookEventJournal(directory).Append(request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Pico Vibe status recovery unavailable: {exception.Message}");
        }
    }

    internal static Dictionary<string, object?> BuildCursorUngatedOutput() => new();

    private static int EmitCursorDecision(HookResponse? response) =>
        Emit(BuildCursorDecision(response));

    internal static Dictionary<string, object?> BuildCursorDecision(
        HookResponse? response)
    {
        var requestedDecision = response?.Decision;
        var failSafeDenied = requestedDecision is null or HookDecision.None or HookDecision.Ask;
        // Once Pico Vibe has elected to gate a Cursor operation, failure to
        // obtain a broker/device/touch decision must not turn into an implicit
        // grant. Some Cursor hook boundaries do not enforce "ask", so use the
        // same fail-safe deny policy for every gate.
        var decision = failSafeDenied
            ? HookDecision.Deny
            : requestedDecision.GetValueOrDefault();
        var value = decision switch
        {
            HookDecision.Allow => "allow",
            HookDecision.Deny => "deny",
            _ => "ask",
        };
        var payload = new Dictionary<string, object?>
        {
            ["permission"] = value,
        };
        if (failSafeDenied)
        {
            const string fallback =
                "Pico Vibe touch approval was unavailable; Cursor blocked the gated operation.";
            payload["user_message"] = fallback;
            payload["agent_message"] = fallback;
        }
        else if (!string.IsNullOrWhiteSpace(response?.Message))
        {
            payload["user_message"] = response.Message;
            if (decision == HookDecision.Deny)
            {
                payload["agent_message"] = response.Message;
            }
        }
        else if (decision == HookDecision.Deny)
        {
            const string fallback = "Pico Vibe denied the gated operation.";
            payload["user_message"] = fallback;
            payload["agent_message"] = fallback;
        }
        return payload;
    }

    private static int EmitNativePermissionDecision(HookResponse? response) =>
        Emit(BuildNativePermissionDecision(response));

    internal static Dictionary<string, object?> BuildCodexDecision(HookResponse? response)
        => BuildNativePermissionDecision(response);

    internal static Dictionary<string, object?> BuildClaudeDecision(HookResponse? response)
        => BuildNativePermissionDecision(response);

    internal static Dictionary<string, object?> BuildNativePermissionDecision(
        HookResponse? response)
    {
        if (response is null || response.Decision is HookDecision.None or HookDecision.Ask)
        {
            return new Dictionary<string, object?>();
        }
        var behavior = response.Decision == HookDecision.Allow ? "allow" : "deny";
        var decision = new Dictionary<string, object?>
        {
            ["behavior"] = behavior,
        };
        // Both native schemas reserve message for a denial explanation.  The
        // broker also supplies a friendly message for allow, but serializing it
        // there makes current Codex/Claude validators reject the hook output.
        if (response.Decision == HookDecision.Deny &&
            !string.IsNullOrWhiteSpace(response.Message))
        {
            decision["message"] = response.Message;
        }
        return new Dictionary<string, object?>
        {
            ["hookSpecificOutput"] = new Dictionary<string, object?>
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = decision,
            },
        };
    }

    private static async Task<int> RunControlAsync(string[] args)
    {
        var control = args.FirstOrDefault()?.ToLowerInvariant();
        if (control is not ("show" or "shutdown"))
        {
            return 2;
        }
        var request = new HookRequest
        {
            Platform = HookPlatform.Codex,
            Event = HookEvent.SessionStarted,
            Metadata = new Dictionary<string, string> { ["control"] = control },
        };
        var response = await SendToAppAsync(request, TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        return response is null ? 1 : 0;
    }

    private static async Task<HookResponse?> SendToAppAsync(
        HookRequest request,
        TimeSpan timeout)
    {
        return await new HookPipeClient(TestPipeName()).SendAsync(
            request,
            connectTimeout: TimeSpan.FromMilliseconds(750),
            responseTimeout: timeout).ConfigureAwait(false);
    }

    internal static string TestPipeName()
    {
        var home = Environment.GetEnvironmentVariable(HookInstaller.TestHomeEnvironmentVariable);
        return string.IsNullOrWhiteSpace(home) ? NamedPipeNames.Default
            : "PicoVibe.Test." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home)))[..16];
    }

    private static async Task<AppConfig> LoadConfigAsync()
    {
        try
        {
            var testHome = Environment.GetEnvironmentVariable(HookInstaller.TestHomeEnvironmentVariable);
            return await new ConfigStore(string.IsNullOrWhiteSpace(testHome) ? null : Path.Combine(testHome, "config.json"))
                .LoadAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException)
        {
            return AppConfig.CreateDefault();
        }
    }

    private static async Task<JsonElement> ReadStdinJsonAsync()
    {
        try
        {
            var text = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
        }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static string GetString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static AiState ParseState(string value) => value.ToLowerInvariant() switch
    {
        "idle" => AiState.Idle,
        "thinking" => AiState.Thinking,
        "tools" => AiState.Tools,
        "ask" or "permission" => AiState.Permission,
        "done" => AiState.Done,
        "error" => AiState.Error,
        _ => AiState.Tools,
    };

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static int Emit(Dictionary<string, object?> payload)
    {
        var clean = payload
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Console.Out.WriteLine(JsonSerializer.Serialize(clean));
        return 0;
    }
}
