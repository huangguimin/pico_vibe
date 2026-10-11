using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PicoVibe.Core.Ipc;

namespace PicoVibe.Hook;

/// <summary>
/// A hook-specific pipe client with separate connect and response deadlines.
/// When the tray app is absent we fall back in under a second instead of
/// waiting for the entire physical-approval timeout before trying the device.
/// </summary>
internal sealed class HookPipeClient(
    string pipeName = NamedPipeNames.Default,
    string serverName = ".")
{
    public async Task<HookResponse?> SendAsync(
        HookRequest request,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }
        if (responseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                serverName,
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using (var connectSource = new CancellationTokenSource(connectTimeout))
            {
                await pipe.ConnectAsync(connectSource.Token).ConfigureAwait(false);
            }

            using var responseSource = new CancellationTokenSource(responseTimeout);
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
            await writer.WriteLineAsync(
                HookJson.Serialize(request).AsMemory(),
                responseSource.Token).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(responseSource.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var response = HookJson.Deserialize<HookResponse>(line);
            return response is not null &&
                string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal)
                ? response
                : null;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or UnauthorizedAccessException or
                TimeoutException or JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
