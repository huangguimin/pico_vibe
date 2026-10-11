using System.Text.Json;
using PicoVibe.App.Services;
using PicoVibe.Core.Configuration;

namespace PicoVibe.App.Tests;

public sealed class AppConfigMergeTests
{
    [Fact]
    public void MusicSensitivityUiLimitMatchesConfigurationNormalization()
    {
        var config = AppConfig.CreateDefault();
        config.Music.Sensitivity = double.MaxValue;

        AppConfigDefaults.Normalize(config);

        Assert.Equal(UiLimits.MaximumMusicSensitivity, config.Music.Sensitivity);
    }

    [Fact]
    public void PreserveLocalDraftKeepsDraftAndIndependentTouchChanges()
    {
        var baseline = AppConfig.CreateDefault();
        var local = Clone(baseline);
        var remote = Clone(baseline);
        var idleProfile = baseline.Ai.StateProfiles[AiState.Idle];

        local.Ai.ApprovalTimeoutSeconds = 37;
        local.Profiles[idleProfile].Brightness = 23;
        remote.Global.LightsEnabled = !baseline.Global.LightsEnabled;
        remote.SystemMonitor.Metric = SystemMetric.MemoryUsage;

        var merged = AppConfigMerge.PreserveLocalDraft(baseline, local, remote);

        Assert.Equal(37, merged.Ai.ApprovalTimeoutSeconds);
        Assert.Equal(23, merged.Profiles[idleProfile].Brightness);
        Assert.Equal(remote.Global.LightsEnabled, merged.Global.LightsEnabled);
        Assert.Equal(SystemMetric.MemoryUsage, merged.SystemMonitor.Metric);
    }

    [Fact]
    public void PreserveLocalDraftWinsSameFieldConflictWithoutSharingInputs()
    {
        var baseline = AppConfig.CreateDefault();
        var local = Clone(baseline);
        var remote = Clone(baseline);
        local.Global.BrightnessPercent = 18;
        remote.Global.BrightnessPercent = 92;
        remote.Global.ActiveMode = PrimaryMode.Music;

        var merged = AppConfigMerge.PreserveLocalDraft(baseline, local, remote);

        Assert.Equal(18, merged.Global.BrightnessPercent);
        Assert.Equal(PrimaryMode.Music, merged.Global.ActiveMode);
        remote.Global.ActiveMode = PrimaryMode.Ai;
        Assert.Equal(PrimaryMode.Music, merged.Global.ActiveMode);
    }

    private static AppConfig Clone(AppConfig source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, ConfigStore.JsonOptions);
        return AppConfigDefaults.Normalize(
            JsonSerializer.Deserialize<AppConfig>(bytes, ConfigStore.JsonOptions));
    }
}
