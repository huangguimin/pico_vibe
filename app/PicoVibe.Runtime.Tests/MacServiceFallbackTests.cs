using PicoVibe.App.Services;
using PicoVibe.Core.Configuration;

namespace PicoVibe.Runtime.Tests;

public sealed class MacServiceFallbackTests
{
    [Fact]
    public async Task MetricsFailCleanlyOutsideMacOs()
    {
        if (OperatingSystem.IsMacOS()) return;
        var service = new MacSystemMetricService();

        MetricSample cpu = await service.GetSampleAsync(SystemMetric.CpuUsage);
        MetricSample memory = await service.GetSampleAsync(SystemMetric.MemoryUsage);
        MetricSample battery = await service.GetSampleAsync(SystemMetric.BatteryLevel);

        Assert.False(cpu.Available);
        Assert.False(memory.Available);
        Assert.False(battery.Available);
        Assert.Equal("macOS Mach host_statistics", cpu.Source);
        Assert.Equal("macOS sysctl", memory.Source);
        Assert.Equal("macOS pmset", battery.Source);
    }

    [Theory]
    [InlineData("Now drawing from 'Battery Power'\n -InternalBattery-0\t84%; discharging; 4:53 remaining present: true", 84, "On battery")]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0\t72%; charging; 1:20 remaining present: true", 72, "Charging")]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0\t100%; charged; 0:00 remaining present: true", 100, "Fully charged")]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0\t91%; not charging; present: true", 91, "Plugged in, not charging")]
    [InlineData("Now drawing from 'AC Power'\n -InternalBattery-0\t98%; discharging; 10:25 remaining present: true", 98, "Plugged in, discharging")]
    public void BatteryOutputIncludesLevelAndPowerState(
        string output,
        int expectedPercent,
        string expectedState)
    {
        MetricSample sample = MacSystemMetricService.ParseBatteryOutput(output);

        Assert.True(sample.Available);
        Assert.Equal(expectedPercent, sample.Value);
        Assert.Contains(expectedState, sample.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void BatteryOutputWithoutInternalBatteryFailsCleanly()
    {
        MetricSample sample = MacSystemMetricService.ParseBatteryOutput(
            "Now drawing from 'AC Power'\n");

        Assert.False(sample.Available);
        Assert.Null(sample.Value);
        Assert.Contains("No internal battery", sample.Reason, StringComparison.Ordinal);
        Assert.Equal("macOS pmset", sample.Source);
    }

    [Fact]
    public void TemperatureBridgeOutputPreservesAvailableAndUnavailableSensors()
    {
        const string output = """
            harmless helper diagnostic
            {"cpu":{"value":46.54,"source":"macOS IOHID SoC die sensors","reason":null},"gpu":{"value":null,"source":"macOS IOHID GPU sensors","reason":"No explicit GPU sensor."},"disk":{"value":38,"source":"macOS IOHID NAND sensors","reason":null}}
            """;

        MacSystemMetricService.MacTemperatureSnapshot snapshot =
            MacSystemMetricService.ParseTemperatureBridgeOutput(output);

        Assert.True(snapshot.Cpu.Available);
        Assert.Equal(46.54, snapshot.Cpu.Value);
        Assert.Equal("macOS IOHID SoC die sensors", snapshot.Cpu.Source);
        Assert.False(snapshot.Gpu.Available);
        Assert.Equal("No explicit GPU sensor.", snapshot.Gpu.Reason);
        Assert.Equal("macOS IOHID GPU sensors", snapshot.Gpu.Source);
        Assert.True(snapshot.Disk.Available);
        Assert.Equal(38, snapshot.Disk.Value);
    }

    [Fact]
    public void TemperatureBridgeOutputKeepsAppleSmcGpuReading()
    {
        const string output =
            "{\"cpu\":{\"value\":44.1,\"source\":\"macOS IOHID SoC die sensors\",\"reason\":null}," +
            "\"gpu\":{\"value\":39.98,\"source\":\"macOS AppleSMC GPU sensors\",\"reason\":null}," +
            "\"disk\":{\"value\":35,\"source\":\"macOS IOHID NAND sensors\",\"reason\":null}}";

        MacSystemMetricService.MacTemperatureSnapshot snapshot =
            MacSystemMetricService.ParseTemperatureBridgeOutput(output);

        Assert.True(snapshot.Gpu.Available);
        Assert.Equal(39.98, snapshot.Gpu.Value);
        Assert.Equal("macOS AppleSMC GPU sensors", snapshot.Gpu.Source);
        Assert.Empty(snapshot.Gpu.Reason);
    }

    [Fact]
    public void TemperatureBridgeOutputRejectsImplausibleOrMissingValues()
    {
        const string output =
            "{\"cpu\":{\"value\":999,\"source\":\"IOHID\",\"reason\":null}," +
            "\"gpu\":{\"value\":null,\"source\":\"IOHID\",\"reason\":null}}";

        MacSystemMetricService.MacTemperatureSnapshot snapshot =
            MacSystemMetricService.ParseTemperatureBridgeOutput(output);

        Assert.False(snapshot.Cpu.Available);
        Assert.Contains("plausible CPU or SoC", snapshot.Cpu.Reason, StringComparison.Ordinal);
        Assert.False(snapshot.Gpu.Available);
        Assert.Contains("plausible GPU", snapshot.Gpu.Reason, StringComparison.Ordinal);
        Assert.False(snapshot.Disk.Available);
        Assert.Contains("no internal NAND", snapshot.Disk.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TemperatureBridgeMalformedOutputFailsCleanly()
    {
        MacSystemMetricService.MacTemperatureSnapshot snapshot =
            MacSystemMetricService.ParseTemperatureBridgeOutput("not json\n{\"peak\":0}");

        Assert.False(snapshot.Cpu.Available);
        Assert.False(snapshot.Gpu.Available);
        Assert.False(snapshot.Disk.Available);
        Assert.Contains("no valid temperature JSON", snapshot.Cpu.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaControlFailsCleanlyOutsideMacOs()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.False(new MacSystemMediaController().SendNextTrack());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(1, 4)]
    [InlineData(99, 4)]
    public void MediaBridgeExitCodesPreserveFailureMeaning(int exitCode, int expected)
    {
        Assert.Equal(expected, (int)MacSystemMediaController.ClassifyBridgeExitCode(exitCode));
    }

    [Fact]
    public async Task LoginItemFailsCleanlyOutsideMacOs()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.False(await new MacLaunchAtLoginService().SetEnabledAsync(true));
    }
}
