using System.Text.Json;
using PicoVibe.Core.Configuration;

namespace PicoVibe.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Defaults_AreProductSafeAndComplete()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.True(config.Global.LightsEnabled);
        Assert.True(config.Global.CloseToTray);
        Assert.Equal(50, config.Global.BrightnessPercent);
        Assert.Equal(CursorShellGate.Never, config.Ai.CursorShellGate);
        Assert.Equal(CursorMcpGate.Never, config.Ai.CursorMcpGate);
        Assert.False(config.Ai.GateFileTools);
        Assert.True(config.Ai.CodexTouchApproval);
        Assert.Equal(90, config.Ai.ApprovalTimeoutSeconds);
        Assert.Equal(0, config.SystemMonitor.DisplayMinimum);
        Assert.Equal(100, config.SystemMonitor.DisplayMaximum);
        Assert.All(Enum.GetValues<AiState>(), state =>
        {
            var profileId = config.Ai.StateProfiles[state];
            Assert.True(config.Profiles.ContainsKey(profileId));
        });
        Assert.Equal(
            TouchAction.DenyRequest,
            config.Touch.Contexts[TouchContext.Permission].LongPress);
        Assert.Equal(
            TouchAction.NextTrack,
            config.Touch.Contexts[TouchContext.Music].DoubleClick);
    }

    [Fact]
    public void Normalize_ClampsRangesAndRestoresMissingMappings()
    {
        var config = AppConfig.CreateDefault();
        config.Global.BrightnessPercent = 500;
        config.Global.LedCount = 0;
        config.Ai.ApprovalTimeoutSeconds = -10;
        config.SystemMonitor.DisplayMinimum = 70;
        config.SystemMonitor.DisplayMaximum = 10;
        config.Music.FramesPerSecond = 100;
        config.Ai.StateProfiles[AiState.Permission] = "missing";
        config.Touch.Contexts.Remove(TouchContext.Music);

        AppConfigDefaults.Normalize(config);

        Assert.Equal(100, config.Global.BrightnessPercent);
        Assert.Equal(1, config.Global.LedCount);
        Assert.Equal(5, config.Ai.ApprovalTimeoutSeconds);
        Assert.Equal(170, config.SystemMonitor.DisplayMaximum);
        Assert.Equal(30, config.Music.FramesPerSecond);
        Assert.Equal("ai-permission", config.Ai.StateProfiles[AiState.Permission]);
        Assert.True(config.Touch.Contexts.ContainsKey(TouchContext.Music));
    }

    [Fact]
    public async Task Store_RoundTripsAndLeavesNoTemporaryFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("config.json");
        var store = new ConfigStore(path);
        var config = AppConfig.CreateDefault();
        config.Global.BrightnessPercent = 37;
        config.Global.LightsEnabled = false;
        config.Global.CloseToTray = false;
        config.Ai.CursorShellGate = CursorShellGate.All;
        config.Ai.CursorMcpGate = CursorMcpGate.Never;
        config.Ai.GateFileTools = true;
        config.Ai.CodexTouchApproval = false;
        config.Ai.ApprovalTimeoutSeconds = 123;
        config.SystemMonitor.DisplayMinimum = 30;
        config.SystemMonitor.DisplayMaximum = 95;

        await store.SaveAsync(AppConfig.CreateDefault());
        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();

        Assert.Equal(37, loaded.Global.BrightnessPercent);
        Assert.False(loaded.Global.LightsEnabled);
        Assert.False(loaded.Global.CloseToTray);
        Assert.Equal(CursorShellGate.All, loaded.Ai.CursorShellGate);
        Assert.Equal(CursorMcpGate.Never, loaded.Ai.CursorMcpGate);
        Assert.True(loaded.Ai.GateFileTools);
        Assert.False(loaded.Ai.CodexTouchApproval);
        Assert.Equal(123, loaded.Ai.ApprovalTimeoutSeconds);
        Assert.Equal(30, loaded.SystemMonitor.DisplayMinimum);
        Assert.Equal(95, loaded.SystemMonitor.DisplayMaximum);
        Assert.Equal(
            TouchAction.NextTrack,
            loaded.Touch.Contexts[TouchContext.Music].DoubleClick);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("all", json.RootElement.GetProperty("ai").GetProperty("cursorShellGate").GetString());
        Assert.Equal(
            "nextTrack",
            json.RootElement
                .GetProperty("touch")
                .GetProperty("contexts")
                .GetProperty("music")
                .GetProperty("doubleClick")
                .GetString());
    }

    [Fact]
    public async Task Store_RejectsNewerSchema()
    {
        using var directory = new TestDirectory();
        var path = directory.File("config.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":999}");
        var store = new ConfigStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }
}
