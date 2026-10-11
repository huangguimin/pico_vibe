using System.IO.Pipes;
using System.Text;

namespace PicoVibe.Core.Ipc;

public interface IHookClient
{
    /// <summary>
    /// Sends one request. Returns null if the app is absent, unreachable, or
    /// does not answer before the timeout so hooks can fail open.
    /// </summary>
    Task<HookResponse?> SendAsync(
        HookRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public static class NamedPipeNames
{
    public const string Default = "PicoVibe.Agent.v1";
}

public sealed class NamedPipeHookClient : IHookClient
{
    private readonly string pipeName;
    private readonly string serverName;

    public NamedPipeHookClient(
        string pipeName = NamedPipeNames.Default,
        string serverName = ".")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        this.pipeName = pipeName;
        this.serverName = serverName;
    }

    public async Task<HookResponse?> SendAsync(
        HookRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                serverName,
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                1024,
                leaveOpen: true);

            await writer.WriteLineAsync(HookJson.Serialize(request).AsMemory(), linked.Token)
                .ConfigureAwait(false);
            var responseLine = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return null;
            }

            var response = HookJson.Deserialize<HookResponse>(responseLine);
            return response is not null &&
                string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal)
                ? response
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
