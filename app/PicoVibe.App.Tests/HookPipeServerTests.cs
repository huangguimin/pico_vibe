using PicoVibe.App.Services;
using PicoVibe.Core.Ipc;

namespace PicoVibe.App.Tests;

public sealed class HookPipeServerTests
{
    [Fact]
    public async Task ClientDisconnectCancelsPendingHandlerPromptly()
    {
        var pipeName = $"pv-{Guid.NewGuid():N}"[..19];
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new HookPipeServer(
            async (request, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return HookResponse.NoDecision(request);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            },
            pipeName);
        server.Start();

        var client = new NamedPipeHookClient(pipeName);
        var response = await client.SendAsync(
            new HookRequest
            {
                Platform = HookPlatform.Codex,
                Event = HookEvent.PermissionRequested,
            },
            TimeSpan.FromMilliseconds(250));

        Assert.Null(response);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
