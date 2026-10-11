using System.Buffers.Binary;

namespace PicoVibe.Core.Device;

public enum VibePacketKind : byte
{
    Frame = 1,
    Release = 2,
}

/// <summary>Encodes the firmware's strict, little-endian VIBE V1 sector.</summary>
public static class VibePacketEncoder
{
    public const int SectorSize = 512;
    public const int HeaderSize = 32;
    public const int LedCount = 12;
    public const int RgbBytes = LedCount * 3;
    public const int RgbOffset = 32;
    public const int CrcOffset = 508;
    public const uint MaximumTtlMilliseconds = 60_000;

    public static byte[] EncodeFrame(
        uint sessionEpoch,
        uint sequence,
        uint ttlMilliseconds,
        byte globalBrightness,
        ReadOnlySpan<byte> rgb888)
    {
        ValidateKey(sessionEpoch, sequence);
        if (ttlMilliseconds is 0 or > MaximumTtlMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttlMilliseconds),
                $"TTL must be between 1 and {MaximumTtlMilliseconds} ms.");
        }

        if (rgb888.Length != RgbBytes)
        {
            throw new ArgumentException($"VIBE V1 requires exactly {RgbBytes} RGB bytes.", nameof(rgb888));
        }

        var packet = CreateHeader(VibePacketKind.Frame, sessionEpoch, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16, 4), ttlMilliseconds);
        packet[20] = globalBrightness;
        packet[21] = LedCount;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(22, 2), RgbBytes);
        rgb888.CopyTo(packet.AsSpan(RgbOffset, RgbBytes));
        WriteCrc(packet);
        return packet;
    }

    public static byte[] EncodeRelease(uint sessionEpoch, uint sequence)
    {
        ValidateKey(sessionEpoch, sequence);
        var packet = CreateHeader(VibePacketKind.Release, sessionEpoch, sequence);
        WriteCrc(packet);
        return packet;
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffff_ffff;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = (uint)-(int)(crc & 1);
                crc = (crc >> 1) ^ (0xedb8_8320 & mask);
            }
        }

        return crc ^ 0xffff_ffff;
    }

    private static byte[] CreateHeader(VibePacketKind kind, uint sessionEpoch, uint sequence)
    {
        var packet = new byte[SectorSize];
        "VIBE"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), sessionEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), sequence);
        return packet;
    }

    private static void WriteCrc(byte[] packet)
    {
        var crc = ComputeCrc32(packet.AsSpan(0, CrcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(CrcOffset, 4), crc);
    }

    private static void ValidateKey(uint sessionEpoch, uint sequence)
    {
        if (sessionEpoch == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionEpoch), "Session epoch must be non-zero.");
        }

        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be non-zero.");
        }
    }
}
