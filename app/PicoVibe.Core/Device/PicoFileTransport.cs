using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PicoVibe.Core.Device;

public interface IPicoDeviceTransport : IAsyncDisposable
{
    ValueTask WriteRgbCommandAsync(
        PicoDeviceLocation device,
        string command,
        CancellationToken cancellationToken = default);

    ValueTask WriteLedFrameAsync(
        PicoDeviceLocation device,
        ReadOnlyMemory<byte> rgb888,
        CancellationToken cancellationToken = default);

    ValueTask<string?> ReadTouchTextAsync(
        PicoDeviceLocation device,
        CancellationToken cancellationToken = default);

    ValueTask DeleteLedFrameAsync(
        PicoDeviceLocation device,
        CancellationToken cancellationToken = default);

    /// <summary>Writes one complete packet to the alternating fixed VIBE mailboxes.</summary>
    ValueTask WriteVibePacketAsync(
        PicoDeviceLocation device,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a release packet, then flushes and closes both mailboxes.</summary>
    ValueTask ReleaseVibeAsync(
        PicoDeviceLocation device,
        ReadOnlyMemory<byte> releasePacket,
        CancellationToken cancellationToken = default);

    /// <summary>Closes cached handles so a remounted device can be reopened at the same path.</summary>
    ValueTask ResetConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// File-based transport for the mounted RAM disk. LED.BIN is kept open and
/// overwritten in place so streaming does not truncate the FAT entry per frame.
/// </summary>
public sealed class PicoFileTransport : IPicoDeviceTransport
{
    private const int MaximumFrameBytes = 64 * 3;
    private const int TouchSectorBytes = 512;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const int MacFNoCache = 48;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? frameStream;
    private string? framePath;
    private FileStream? touchStream;
    private string? touchPath;
    private readonly FileStream?[] vibeStreams = new FileStream?[2];
    private string? vibeRoot;
    private int nextVibeSlot;
    private bool disposed;

    public async ValueTask WriteRgbCommandAsync(
        PicoDeviceLocation device,
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ThrowIfDisposed();
        if (command.Any(character => character > 0x7f))
        {
            throw new ArgumentException("RGB.INI commands must contain ASCII characters only.", nameof(command));
        }

        var data = Encoding.ASCII.GetBytes(command.TrimEnd('\r', '\n') + "\r\n");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMounted(device);
            await using var stream = new FileStream(
                device.RgbIniPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                512,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask WriteLedFrameAsync(
        PicoDeviceLocation device,
        ReadOnlyMemory<byte> rgb888,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        if (rgb888.Length is < 3 or > MaximumFrameBytes || rgb888.Length % 3 != 0)
        {
            throw new ArgumentException(
                "LED frame must contain 1..64 complete RGB888 pixels.",
                nameof(rgb888));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMounted(device);
            await EnsureFrameStreamAsync(device.LedBinPath, rgb888.Length).ConfigureAwait(false);
            frameStream!.Position = 0;
            await frameStream.WriteAsync(rgb888, cancellationToken).ConfigureAwait(false);
            await frameStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            frameStream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await ResetConnectionUnderLockAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<string?> ReadTouchTextAsync(
        PicoDeviceLocation device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        if (!File.Exists(device.TouchPath))
        {
            return null;
        }

        // The Pico updates this fixed sector behind the host filesystem. Windows
        // may otherwise keep returning its cached copy forever, even though a
        // newly opened ordinary FileStream is used on every poll.
        if (OperatingSystem.IsWindows())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unbuffered = TryReadTouchTextWindowsUnbuffered(device.TouchPath);
            if (unbuffered is not null)
            {
                return unbuffered;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            return await ReadTouchTextMacUnbufferedAsync(device, cancellationToken)
                .ConfigureAwait(false);
        }

        var buffer = new byte[TouchSectorBytes];
        try
        {
            await using var stream = new FileStream(
                device.TouchPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                TouchSectorBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return DecodeTouchText(buffer.AsSpan(0, read));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async ValueTask DeleteLedFrameAsync(
        PicoDeviceLocation device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(framePath, device.LedBinPath, StringComparison.OrdinalIgnoreCase))
            {
                await CloseFrameStreamAsync().ConfigureAwait(false);
            }

            if (File.Exists(device.LedBinPath))
            {
                File.Delete(device.LedBinPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask WriteVibePacketAsync(
        PicoDeviceLocation device,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        if (packet.Length != VibePacketEncoder.SectorSize)
        {
            throw new ArgumentException("A VIBE V1 mailbox packet must be exactly 512 bytes.", nameof(packet));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMounted(device);
            await WriteVibePacketUnderLockAsync(device, packet, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await ResetConnectionUnderLockAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ReleaseVibeAsync(
        PicoDeviceLocation device,
        ReadOnlyMemory<byte> releasePacket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        if (releasePacket.Length != VibePacketEncoder.SectorSize)
        {
            throw new ArgumentException(
                "A VIBE V1 release packet must be exactly 512 bytes.",
                nameof(releasePacket));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMounted(device);
            await WriteVibePacketUnderLockAsync(device, releasePacket, cancellationToken)
                .ConfigureAwait(false);
            await CloseVibeStreamsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await ResetConnectionUnderLockAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ResetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ResetConnectionUnderLockAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed)
            {
                disposed = true;
                await ResetConnectionUnderLockAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask EnsureFrameStreamAsync(string path, int length)
    {
        if (frameStream is null ||
            !string.Equals(framePath, path, StringComparison.OrdinalIgnoreCase))
        {
            await CloseFrameStreamAsync().ConfigureAwait(false);
            frameStream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                512,
                FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.RandomAccess);
            DisableMacFileCache(frameStream);
            framePath = path;
        }

        if (frameStream.Length != length)
        {
            frameStream.SetLength(length);
        }
    }

    private async ValueTask CloseFrameStreamAsync()
    {
        var stream = frameStream;
        frameStream = null;
        framePath = null;
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask WriteVibePacketUnderLockAsync(
        PicoDeviceLocation device,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        await EnsureVibeStreamsAsync(device).ConfigureAwait(false);
        var slot = nextVibeSlot;
        nextVibeSlot = (nextVibeSlot + 1) & 1;
        var stream = vibeStreams[slot]!;
        stream.Position = 0;
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async ValueTask EnsureVibeStreamsAsync(PicoDeviceLocation device)
    {
        if (string.Equals(vibeRoot, device.RootPath, StringComparison.OrdinalIgnoreCase) &&
            vibeStreams[0] is not null &&
            vibeStreams[1] is not null)
        {
            return;
        }

        await CloseVibeStreamsAsync().ConfigureAwait(false);
        var paths = new[] { device.VibeSlot0Path, device.VibeSlot1Path };
        try
        {
            for (var index = 0; index < paths.Length; index++)
            {
                var stream = new FileStream(
                    paths[index],
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    VibePacketEncoder.SectorSize,
                    FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.RandomAccess);
                DisableMacFileCache(stream);
                if (stream.Length != VibePacketEncoder.SectorSize)
                {
                    stream.Dispose();
                    throw new InvalidDataException($"VIBE mailbox {paths[index]} is not 512 bytes.");
                }

                vibeStreams[index] = stream;
            }

            vibeRoot = device.RootPath;
            nextVibeSlot = 0;
        }
        catch
        {
            await CloseVibeStreamsAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask CloseVibeStreamsAsync()
    {
        var streams = vibeStreams.ToArray();
        Array.Clear(vibeStreams);
        vibeRoot = null;
        nextVibeSlot = 0;
        Exception? failure = null;
        foreach (var stream in streams)
        {
            if (stream is null)
            {
                continue;
            }

            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async ValueTask ResetConnectionUnderLockAsync()
    {
        var cachedFrame = frameStream;
        frameStream = null;
        framePath = null;
        var cachedTouch = touchStream;
        touchStream = null;
        touchPath = null;
        var cachedVibeStreams = vibeStreams.ToArray();
        Array.Clear(vibeStreams);
        vibeRoot = null;
        nextVibeSlot = 0;

        await DisposeBestEffortAsync(cachedFrame).ConfigureAwait(false);
        await DisposeBestEffortAsync(cachedTouch).ConfigureAwait(false);
        foreach (var stream in cachedVibeStreams)
        {
            await DisposeBestEffortAsync(stream).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeBestEffortAsync(FileStream? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The mounted volume may already be gone. References were cleared
            // before disposal so the next connection always opens fresh handles.
        }
    }

    private static void EnsureMounted(PicoDeviceLocation device)
    {
        if (!PicoDeviceCompatibility.IsAccessible(device))
        {
            throw new DirectoryNotFoundException($"PICO_RGB is not mounted at {device.RootPath}.");
        }
    }

    private static string? TryReadTouchTextWindowsUnbuffered(string path)
    {
        using var handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagRandomAccess,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var allocation = Marshal.AllocHGlobal(TouchSectorBytes * 2 - 1);
        try
        {
            var alignedAddress =
                (allocation.ToInt64() + TouchSectorBytes - 1) & ~(TouchSectorBytes - 1L);
            var aligned = new IntPtr(alignedAddress);
            if (!ReadFile(handle, aligned, TouchSectorBytes, out var bytesRead, IntPtr.Zero) ||
                bytesRead <= 0)
            {
                return null;
            }

            var buffer = new byte[bytesRead];
            Marshal.Copy(aligned, buffer, 0, bytesRead);
            return DecodeTouchText(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(allocation);
        }
    }

    private async ValueTask<string?> ReadTouchTextMacUnbufferedAsync(
        PicoDeviceLocation device,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMounted(device);
            if (touchStream is null ||
                !string.Equals(touchPath, device.TouchPath, StringComparison.OrdinalIgnoreCase))
            {
                await DisposeBestEffortAsync(touchStream).ConfigureAwait(false);
                touchStream = new FileStream(
                    device.TouchPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    TouchSectorBytes,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                DisableMacFileCache(touchStream);
                touchPath = device.TouchPath;
            }

            touchStream.Position = 0;
            var buffer = new byte[TouchSectorBytes];
            var read = await touchStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return DecodeTouchText(buffer.AsSpan(0, read));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void DisableMacFileCache(FileStream stream)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
        _ = MacFcntl(descriptor, MacFNoCache, 1);
    }

    private static string DecodeTouchText(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0)
        {
            bytes = bytes[..terminator];
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle file,
        IntPtr buffer,
        int bytesToRead,
        out int bytesRead,
        IntPtr overlapped);

    [DllImport("libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int MacFcntl(int fileDescriptor, int command, int value);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
