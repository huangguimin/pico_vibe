using System.Text.Json;
using System.Text.Json.Serialization;

namespace PicoVibe.Core.Ipc;

public enum HookPlatform
{
    Cursor,
    Codex,
    Claude,
}

public enum HookEvent
{
    SessionStarted,
    PromptSubmitted,
    ToolStarted,
    PermissionRequested,
    ToolCompleted,
    ToolFailed,
    AgentStopped,
    SessionEnded,
    PermissionResolved,
}

public enum HookDecision
{
    None,
    Allow,
    Deny,
    Ask,
}

public sealed record HookRequest
{
    public const string CurrentProtocol = "pico-vibe/1";

    public string Protocol { get; init; } = CurrentProtocol;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public HookPlatform Platform { get; init; }
    public HookEvent Event { get; init; }
    public string? ProjectName { get; init; }
    public string? AgentName { get; init; }
    public string? SessionId { get; init; }
    public int? OwnerProcessId { get; init; }
    public long? OwnerProcessStartUtcTicks { get; init; }
    public string? AgentId { get; init; }
    public string? TurnId { get; init; }
    public string? ToolUseId { get; init; }
    public string? ToolFingerprint { get; init; }
    public string? PermissionRequestId { get; init; }
    public HookDecision Decision { get; init; }
    public bool ObserveOnly { get; init; }
    public string? ToolName { get; init; }
    public bool? Sandboxed { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record HookResponse
{
    public string Protocol { get; init; } = HookRequest.CurrentProtocol;
    public string RequestId { get; init; } = string.Empty;
    public HookDecision Decision { get; init; }
    public string? Message { get; init; }

    public static HookResponse NoDecision(HookRequest request, string? message = null) => new()
    {
        RequestId = request.RequestId,
        Decision = HookDecision.None,
        Message = message,
    };
}

public static class HookJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
