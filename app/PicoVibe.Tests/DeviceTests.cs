using PicoVibe.Core.Device;

namespace PicoVibe.Tests;

public sealed class DeviceTests
{
    [Fact]
    public async Task Locator_AcceptsExplicitRootOrKnownFile()
    {
        using var directory = new TestDirectory();
        var touch = directory.File("TOUCH.TXT");
        await File.WriteAllTextAsync(touch, "seq=0\nevt=none\nt_ms=0\n");
        await File.WriteAllBytesAsync(
            directory.File("VIBE0.BIN"),
            new byte[VibePacketEncoder.SectorSize]);
        await File.WriteAllBytesAsync(
            directory.File("VIBE1.BIN"),
            new byte[VibePacketEncoder.SectorSize]);
        var locator = new PicoRamLocator();

        var fromRoot = await locator.FindAsync(directory.Path);
        var fromFile = await locator.FindAsync(touch);

        Assert.Equal(Path.GetFullPath(directory.Path), fromRoot!.RootPath);
        Assert.Equal(fromRoot, fromFile);
    }

    [Fact]
    public async Task Locator_SearchesOneNestedMountLevel()
    {
        using var directory = new TestDirectory();
        var nested = Path.Combine(directory.Path, "user", "PICO_RGB");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "TOUCH.TXT"), "seq=0\nevt=none\nt_ms=0\n");
        await File.WriteAllBytesAsync(
            Path.Combine(nested, "VIBE0.BIN"),
            new byte[VibePacketEncoder.SectorSize]);
        await File.WriteAllBytesAsync(
            Path.Combine(nested, "VIBE1.BIN"),
            new byte[VibePacketEncoder.SectorSize]);
        var locator = new PicoRamLocator(new PicoRamLocatorOptions
        {
            AdditionalSearchRoots = new[] { directory.Path },
        });

        var found = await locator.FindAsync();

        Assert.Equal(Path.GetFullPath(nested), found!.RootPath);
    }

    [Fact]
    public async Task Locator_StillFindsLegacyPicoRamVolume()
    {
        using var directory = new TestDirectory();
        var nested = Path.Combine(directory.Path, "PICO_RAM");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "TOUCH.TXT"), "seq=0\nevt=none\nt_ms=0\n");
        await File.WriteAllBytesAsync(
            Path.Combine(nested, "VIBE0.BIN"),
            new byte[VibePacketEncoder.SectorSize]);
        await File.WriteAllBytesAsync(
            Path.Combine(nested, "VIBE1.BIN"),
            new byte[VibePacketEncoder.SectorSize]);
        var locator = new PicoRamLocator(new PicoRamLocatorOptions
        {
            AdditionalSearchRoots = new[] { directory.Path },
        });

        var found = await locator.FindAsync();

        Assert.Equal(Path.GetFullPath(nested), found!.RootPath);
    }

    [Fact]
    public async Task Locator_RejectsSameNamedVolumeWithoutFixedMailboxes()
    {
        using var directory = new TestDirectory();
        var nested = Path.Combine(directory.Path, "PICO_RGB");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "notes.txt"), "ordinary removable disk");
        var locator = new PicoRamLocator();

        var found = await locator.FindAsync(nested);

        Assert.Null(found);
        Assert.False(File.Exists(Path.Combine(nested, "RGB.INI")));
    }

    [Fact]
    public void Compatibility_IsAccessibleRequiresReadableMailbox()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        File.WriteAllBytes(device.VibeSlot0Path, new byte[VibePacketEncoder.SectorSize]);
        File.WriteAllBytes(device.VibeSlot1Path, new byte[VibePacketEncoder.SectorSize]);
        File.WriteAllText(device.TouchPath, "seq=0\nevt=none\nt_ms=0\n");

        Assert.True(PicoDeviceCompatibility.IsAccessible(device));

        File.Delete(device.VibeSlot0Path);
        Assert.False(PicoDeviceCompatibility.IsAccessible(device));
    }

    [Fact]
    public async Task FileTransport_WritesCommandsFramesTouchAndVibeMailbox()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        await File.WriteAllTextAsync(device.TouchPath, "seq=3\nevt=click\nt_ms=99\n");
        await File.WriteAllBytesAsync(device.VibeSlot0Path, new byte[VibePacketEncoder.SectorSize]);
        await File.WriteAllBytesAsync(device.VibeSlot1Path, new byte[VibePacketEncoder.SectorSize]);
        await using var transport = new PicoFileTransport();

        await transport.WriteRgbCommandAsync(device, "MODE=solid,R=1,G=2,B=3");
        Assert.Equal(
            "MODE=solid,R=1,G=2,B=3\r\n",
            await File.ReadAllTextAsync(device.RgbIniPath));

        await transport.WriteLedFrameAsync(device, new byte[] { 1, 2, 3, 4, 5, 6 });
        await transport.WriteLedFrameAsync(device, new byte[] { 7, 8, 9, 10, 11, 12 });
        Assert.Equal(new byte[] { 7, 8, 9, 10, 11, 12 }, await ReadSharedAsync(device.LedBinPath));
        Assert.Contains("evt=click", await transport.ReadTouchTextAsync(device));

        var pixels = Enumerable.Range(0, VibePacketEncoder.RgbBytes).Select(value => (byte)value).ToArray();
        var firstPacket = VibePacketEncoder.EncodeFrame(1, 1, 1000, 128, pixels);
        var secondPacket = VibePacketEncoder.EncodeFrame(1, 2, 1000, 128, pixels);
        var releasePacket = VibePacketEncoder.EncodeRelease(1, 3);
        await transport.WriteVibePacketAsync(device, firstPacket);
        await transport.WriteVibePacketAsync(device, secondPacket);
        await transport.ReleaseVibeAsync(device, releasePacket);
        Assert.Equal(releasePacket, await File.ReadAllBytesAsync(device.VibeSlot0Path));
        Assert.Equal(secondPacket, await File.ReadAllBytesAsync(device.VibeSlot1Path));

        await transport.DeleteLedFrameAsync(device);
        Assert.False(File.Exists(device.LedBinPath));
    }

    [Fact]
    public async Task FileTransport_ReusesTouchPathAndReadsUpdatedContent()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        // macOS reads TOUCH.TXT through the stale-mount guard, which probes
        // VIBE0.BIN first; without it the read returns null on that platform.
        await File.WriteAllBytesAsync(device.VibeSlot0Path, new byte[512]);
        await File.WriteAllTextAsync(device.TouchPath, "seq=1\nevt=click\nt_ms=10\n");
        await using var transport = new PicoFileTransport();

        Assert.Contains("seq=1", await transport.ReadTouchTextAsync(device));
        await File.WriteAllTextAsync(device.TouchPath, "seq=2\nevt=long\nt_ms=20\n");

        var updated = await transport.ReadTouchTextAsync(device);
        Assert.Contains("seq=2", updated);
        Assert.Contains("evt=long", updated);
    }

    [Fact]
    public async Task FileTransport_RejectsMalformedDynamicPayloads()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        await using var transport = new PicoFileTransport();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await transport.WriteLedFrameAsync(device, new byte[] { 1, 2 }));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await transport.WriteVibePacketAsync(device, Array.Empty<byte>()));
    }

    [Fact]
    public async Task FileTransport_ResetConnectionReopensSameMailboxPaths()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var empty = new byte[VibePacketEncoder.SectorSize];
        await File.WriteAllBytesAsync(device.VibeSlot0Path, empty);
        await File.WriteAllBytesAsync(device.VibeSlot1Path, empty);
        await using var transport = new PicoFileTransport();
        var pixels = new byte[VibePacketEncoder.RgbBytes];

        await transport.WriteVibePacketAsync(
            device,
            VibePacketEncoder.EncodeFrame(1, 1, 1000, 128, pixels));
        await transport.WriteVibePacketAsync(
            device,
            VibePacketEncoder.EncodeFrame(1, 2, 1000, 128, pixels));
        await transport.ResetConnectionAsync();

        await File.WriteAllBytesAsync(device.VibeSlot0Path, empty);
        await File.WriteAllBytesAsync(device.VibeSlot1Path, empty);
        var replacement = VibePacketEncoder.EncodeFrame(2, 1, 1000, 64, pixels);
        await transport.WriteVibePacketAsync(device, replacement);

        Assert.Equal(replacement, await ReadSharedAsync(device.VibeSlot0Path));
        Assert.Equal(empty, await ReadSharedAsync(device.VibeSlot1Path));
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            512,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var data = new byte[stream.Length];
        await stream.ReadExactlyAsync(data);
        return data;
    }
}
