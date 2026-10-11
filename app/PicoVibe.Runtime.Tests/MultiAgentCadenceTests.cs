using PicoVibe.App.Services;
using PicoVibe.Core.Configuration;

namespace PicoVibe.Runtime.Tests;

public sealed class MultiAgentCadenceTests
{
    [Fact]
    public void AgentPermissionSettingDoesNotChangeOtherModesCadence()
    {
        var config = AppConfig.CreateDefault();
        config.Ai.DisplayMode = AiDisplayMode.PerAgent;
        config.Global.AlwaysShowAiPermission = true;
        config.Global.ActiveMode = PrimaryMode.Music;
        config.Music.FramesPerSecond = 30;
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 30), LightOrchestrator.RequestedFrameInterval(config, false));
        Assert.Equal(TimeSpan.FromMilliseconds(100), LightOrchestrator.RequestedFrameInterval(config, true));
        config.Global.ActiveMode = PrimaryMode.SystemMonitor;
        config.SystemMonitor.UpdateIntervalMilliseconds = 750;
        Assert.Equal(TimeSpan.FromMilliseconds(750), LightOrchestrator.RequestedFrameInterval(config, false));
    }
}
