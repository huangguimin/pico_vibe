using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using PicoVibe.Core.Ipc;

namespace PicoVibe.App.Services;

/// <summary>
/// Current-user-only IPC endpoint used by the tiny Cursor/Codex/Claude hook helper.
/// Each client sends one JSON line and receives one JSON line. Permission
/// clients may remain connected while the orchestrator waits for a gesture.
/// </summary>
public sealed class HookPipeServer : IAsyncDisposable
{
    private readonly Func<HookRequest, CancellationToken, Task<HookResponse>> handler;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<int, Task> clientTasks = new();
    private readonly string pipeName;
    private Task? acceptLoop;
    private int nextClientId;
    private int startState;
    private int disposeState;

    public HookPipeServer(
        Func<HookRequest, CancellationToken, Task<HookResponse>> handler,
        string pipeName = NamedPipeNames.Default)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        if (Interlocked.CompareExchange(ref startState, 1, 0) != 0)
        {
            return;
        }

        acceptLoop = Task.Run(() => AcceptLoopAsync(shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        shutdown.Cancel();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await Task.WhenAll(clientTasks.Values).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Error("A hook pipe client failed during shutdown.", exception);
        }

        shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var clientId = Interlocked.Increment(ref nextClientId);
                var clientTask = HandleClientAsync(pipe, cancellationToken);
                clientTasks[clientId] = clientTask;
                _ = clientTask.ContinueWith(
                    completedTask =>
                    {
                        _ = clientTasks.TryRemove(clientId, out _);
                        _ = completedTask.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverCancellation)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                4096,
                leaveOpen: true);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                4096,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            try
            {
                var line = await reader.ReadLineAsync(serverCancellation).ConfigureAwait(false);
                var request = string.IsNullOrWhiteSpace(line)
                    ? null
                    : HookJson.Deserialize<HookRequest>(line);
                if (request is null || request.Protocol != HookRequest.CurrentProtocol)
                {
                    return;
                }

                using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
                using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
                var disconnectMonitor = MonitorDisconnectAsync(
                    pipe,
                    handlerCancellation,
                    monitorCancellation.Token);
                HookResponse response;
                try
                {
                    response = await handler(request, handlerCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    monitorCancellation.Cancel();
                    try
                    {
                        await disconnectMonitor.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
                    {
                    }
                }

                await writer.WriteLineAsync(
                    HookJson.Serialize(response).AsMemory(),
                    serverCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // The agent may cancel a request and close its pipe while a
                // touch decision is pending. No user-visible error is needed.
            }
            catch (System.Text.Json.JsonException)
            {
            }
            catch (Exception exception)
            {
                AppLog.Error("Hook pipe client failed.", exception);
            }
        }
    }

    private static async Task MonitorDisconnectAsync(
        NamedPipeServerStream pipe,
        CancellationTokenSource handlerCancellation,
        CancellationToken cancellationToken)
    {
        var probe = new byte[1];
        try
        {
            while (await pipe.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
            {
                // The protocol is one request line. Discard unexpected trailing
                // bytes while continuing to detect the client's close promptly.
            }
            handlerCancellation.Cancel();
        }
        catch (IOException)
        {
            handlerCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            handlerCancellation.Cancel();
        }
    }
}
