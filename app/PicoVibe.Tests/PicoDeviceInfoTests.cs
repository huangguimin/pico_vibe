using PicoVibe.Core.Device;

namespace PicoVibe.Tests;

public sealed class PicoDeviceInfoTests
{
    [Fact]
    public void ParsesEveryFieldTheFirmwareWrites()
    {
        var info = PicoDeviceInfo.Parse(
            "pico_vibe=1\nfw=2.23\nboard=production_v1\nboard_id=2\nproto=vibe1\nleds=12\n");

        Assert.Equal("2.23", info.FirmwareVersion);
        Assert.Equal("production_v1", info.BoardName);
        Assert.Equal(2, info.BoardId);
        Assert.Equal("vibe1", info.Protocol);
        Assert.Equal(12, info.LedCount);
        Assert.False(info.IsLegacy);
    }

    /// <summary>
    /// A newer board may declare fields this build has never heard of. Ignoring
    /// them is what lets one application drive several hardware revisions.
    /// </summary>
    [Fact]
    public void IgnoresUnknownKeysAndKeepsTheRest()
    {
        var info = PicoDeviceInfo.Parse(
            "pico_vibe=1\nfw=3.00\nserial=ABC123\nleds=8\nfuture_thing=42\n");

        Assert.Equal("3.00", info.FirmwareVersion);
        Assert.Equal(8, info.LedCount);
    }

    /// <summary>
    /// Firmware from before INFO.TXT existed has no such file. That is an older
    /// build, not an incompatible device, so it must not be rejected.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a device info file")]
    public void MissingOrForeignContentFallsBackToTheOriginalBoard(string? text)
    {
        var info = PicoDeviceInfo.Parse(text);

        Assert.True(info.IsLegacy);
        Assert.Equal(12, info.LedCount);
        Assert.Equal(1, info.BoardId);
    }

    [Theory]
    [InlineData("pico_vibe=1\nleds=0\n")]
    [InlineData("pico_vibe=1\nleds=-4\n")]
    [InlineData("pico_vibe=1\nleds=lots\n")]
    public void ImplausibleNumbersKeepTheDefault(string text)
    {
        Assert.Equal(12, PicoDeviceInfo.Parse(text).LedCount);
    }

    [Fact]
    public void CarriageReturnsAndSpacingDoNotLeakIntoValues()
    {
        var info = PicoDeviceInfo.Parse("pico_vibe=1\r\nfw= 2.23 \r\nboard=production_v1\r\n");

        Assert.Equal("2.23", info.FirmwareVersion);
        Assert.Equal("production_v1", info.BoardName);
    }
}
