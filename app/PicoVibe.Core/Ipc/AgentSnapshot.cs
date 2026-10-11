using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Ipc;

public sealed record AgentSnapshot(string Key, HookPlatform Platform, string Name,
    AiState State, DateTimeOffset LastActivity, bool Ended = false)
{
    public static string KeyFor(HookRequest request) => KeyFor(request.Platform, request.SessionId ?? "legacy", request.AgentId ?? "");
    public static string KeyFor(HookPlatform platform, string session, string agent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { platform.ToString(), session, agent }))));
}
