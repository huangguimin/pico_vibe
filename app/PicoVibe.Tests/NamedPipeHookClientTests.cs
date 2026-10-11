using System.IO.Pipes;
using System.Text;
using PicoVibe.Core.Ipc;

namespace PicoVibe.Tests;

public sealed class NamedPipeHookClientTests
{
    [Fact]
    public async Task Client_RoundTripsTypedMessage()
    {
        var pipeName = NewPipeName("pv");
        var serverTask = RunServerAsync(pipeName);
        var client = new NamedPipeHookClient(pipeName);
        var request = new HookRequest
        {
            Platform = HookPlatform.Codex,
            Event = HookEvent.PermissionRequested,
            ToolName = "exec_command",
        };

        var response = await client.SendAsync(request, TimeSpan.FromSeconds(3));

        Assert.NotNull(response);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(HookDecision.Allow, response.Decision);
        var received = await serverTask;
        Assert.Equal(HookPlatform.Codex, received.Platform);
        Assert.Equal(HookEvent.PermissionRequested, received.Event);
    }

    [Fact]
    public async Task Client_ReturnsNullWhenAppIsAbsent()
    {
        var client = new NamedPipeHookClient(NewPipeName("pvm"));
        var request = new HookRequest { Platform = HookPlatform.Cursor, Event = HookEvent.ToolStarted };

        var response = await client.SendAsync(request, TimeSpan.FromMilliseconds(100));

        Assert.Null(response);
    }

    private static async Task<HookRequest> RunServerAsync(string pipeName)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(
            server,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            1024,
            leaveOpen: true);
        using var writer = new StreamWriter(
            server,
            new UTF8Encoding(false),
            1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        var line = await reader.ReadLineAsync();
        var request = HookJson.Deserialize<HookRequest>(line!)!;
        var response = new HookResponse
        {
            RequestId = request.RequestId,
            Decision = HookDecision.Allow,
        };
        await writer.WriteLineAsync(HookJson.Serialize(response));
        return request;
    }

    private static string NewPipeName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 17)];
}
