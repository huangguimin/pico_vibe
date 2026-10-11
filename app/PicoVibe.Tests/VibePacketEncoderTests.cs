using System.Buffers.Binary;
using System.Text;
using PicoVibe.Core.Device;

namespace PicoVibe.Tests;

public sealed class VibePacketEncoderTests
{
    [Fact]
    public void Crc32_MatchesStandardCheckVector()
    {
        Assert.Equal(
            0xcbf4_3926u,
            VibePacketEncoder.ComputeCrc32(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void Frame_ExactlyMatchesFirmwareV1Layout()
    {
        var pixels = Enumerable.Range(0, VibePacketEncoder.RgbBytes).Select(value => (byte)value).ToArray();

        var packet = VibePacketEncoder.EncodeFrame(0x1122_3344, 7, 2500, 99, pixels);

        Assert.Equal(VibePacketEncoder.SectorSize, packet.Length);
        Assert.Equal("VIBE", Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal(1, packet[4]);
        Assert.Equal((byte)VibePacketKind.Frame, packet[5]);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2)));
        Assert.Equal(0x1122_3344u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)));
        Assert.Equal(2500u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16, 4)));
        Assert.Equal(99, packet[20]);
        Assert.Equal(12, packet[21]);
        Assert.Equal(36, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(22, 2)));
        Assert.Equal(pixels, packet.AsSpan(VibePacketEncoder.RgbOffset, pixels.Length).ToArray());
        Assert.All(packet[68..508], value => Assert.Equal(0, value));
        Assert.Equal(
            VibePacketEncoder.ComputeCrc32(packet.AsSpan(0, VibePacketEncoder.CrcOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(VibePacketEncoder.CrcOffset, 4)));
    }

    [Fact]
    public void Release_HasNoPayloadAndRejectsInvalidKeys()
    {
        var packet = VibePacketEncoder.EncodeRelease(4, 9);

        Assert.Equal((byte)VibePacketKind.Release, packet[5]);
        Assert.All(packet[16..508], value => Assert.Equal(0, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => VibePacketEncoder.EncodeRelease(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => VibePacketEncoder.EncodeRelease(1, 0));
        Assert.Throws<ArgumentException>(() =>
            VibePacketEncoder.EncodeFrame(1, 1, 1000, 255, new byte[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VibePacketEncoder.EncodeFrame(1, 1, 0, 255, new byte[VibePacketEncoder.RgbBytes]));
    }
}
