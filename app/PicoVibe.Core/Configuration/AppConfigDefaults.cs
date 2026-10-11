using PicoVibe.Core.Lighting;

namespace PicoVibe.Core.Configuration;

/// <summary>Creates and normalizes versioned application configuration.</summary>
public static class AppConfigDefaults
{
    private static readonly IReadOnlyDictionary<AiState, string> AiProfileIds =
        new Dictionary<AiState, string>
        {
            [AiState.Idle] = "ai-idle",
            [AiState.Thinking] = "ai-thinking",
            [AiState.Tools] = "ai-tools",
            [AiState.Permission] = "ai-permission",
            [AiState.Done] = "ai-done",
            [AiState.Error] = "ai-error",
            [AiState.Unknown] = "ai-unknown",
        };

    public static AppConfig Create()
    {
        var config = new AppConfig();
        foreach (var profile in CreateProfiles())
        {
            config.Profiles.Add(profile.Id, profile);
        }

        config.Ai.StateProfiles = new Dictionary<AiState, string>(AiProfileIds);
        config.Touch.Contexts = CreateTouchContexts();
        return config;
    }

    /// <summary>
    /// Adds newly introduced defaults and clamps unsafe values. Existing user
    /// profiles are retained unless an AI mapping points to a missing profile.
    /// </summary>
    public static AppConfig Normalize(AppConfig? config)
    {
        config ??= Create();
        if (config.SchemaVersion > AppConfig.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Configuration schema {config.SchemaVersion} is newer than supported schema " +
                $"{AppConfig.CurrentSchemaVersion}.");
        }

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
        config.Global ??= new GlobalSettings();
        config.Ai ??= new AiSettings();
        config.Ai.MultiAgent = MultiAgentSettings.Normalize(config.Ai.MultiAgent);
        if (!Enum.IsDefined(config.Ai.DisplayMode)) config.Ai.DisplayMode = AiDisplayMode.Overall;
        config.SystemMonitor ??= new SystemMonitorSettings();
        config.Music ??= new MusicSettings();
        config.Touch ??= new TouchSettings();
        config.Profiles = config.Profiles is null
            ? new Dictionary<string, LightProfile>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LightProfile>(config.Profiles, StringComparer.OrdinalIgnoreCase);

        config.Global.BrightnessPercent = Math.Clamp(config.Global.BrightnessPercent, 0, 100);
        config.Global.LedCount = Math.Clamp(config.Global.LedCount, 1, 64);
        config.Ai.ApprovalTimeoutSeconds = Math.Clamp(config.Ai.ApprovalTimeoutSeconds, 5, 600);
        config.Ai.DoneOverlayMilliseconds = Math.Clamp(config.Ai.DoneOverlayMilliseconds, 0, 60_000);
        config.Ai.ErrorOverlayMilliseconds = Math.Clamp(config.Ai.ErrorOverlayMilliseconds, 0, 60_000);
        config.SystemMonitor.UpdateIntervalMilliseconds =
            Math.Clamp(config.SystemMonitor.UpdateIntervalMilliseconds, 100, 5_000);
        if (!double.IsFinite(config.SystemMonitor.DisplayMinimum))
        {
            config.SystemMonitor.DisplayMinimum = 0.0;
        }

        if (!double.IsFinite(config.SystemMonitor.DisplayMaximum) ||
            config.SystemMonitor.DisplayMaximum <= config.SystemMonitor.DisplayMinimum)
        {
            config.SystemMonitor.DisplayMaximum = config.SystemMonitor.DisplayMinimum + 100.0;
        }
        config.Music.FramesPerSecond = Math.Clamp(config.Music.FramesPerSecond, 1, 30);
        config.Music.Sensitivity = Math.Clamp(config.Music.Sensitivity, 0.1, 5.0);
        config.Music.NoiseGate = Math.Clamp(config.Music.NoiseGate, 0.0, 1.0);
        config.Music.Attack = Math.Clamp(config.Music.Attack, 0.0, 1.0);
        config.Music.Release = Math.Clamp(config.Music.Release, 0.0, 1.0);

        var defaults = CreateProfiles().ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, defaultProfile) in defaults)
        {
            if (!config.Profiles.TryGetValue(id, out var profile) || profile is null)
            {
                config.Profiles[id] = defaultProfile;
            }
        }

        foreach (var (id, profile) in config.Profiles.ToArray())
        {
            profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? id : profile.Id.Trim();
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name.Trim();
            profile.Primary = ClampColor(profile.Primary);
            profile.Secondary = ClampColor(profile.Secondary);
            profile.Brightness = Math.Clamp(profile.Brightness, 0, 255);
            profile.Speed = Math.Clamp(profile.Speed, 1, 255);
            profile.Start = Math.Clamp(profile.Start, 0, 63);
            profile.Count = Math.Clamp(profile.Count, 1, 64);
            profile.MaxMilliamps = Math.Clamp(profile.MaxMilliamps, 0, 65_535);
            profile.OnMilliseconds = Math.Clamp(profile.OnMilliseconds, 0, 65_535);
            profile.OffMilliseconds = Math.Clamp(profile.OffMilliseconds, 0, 65_535);
            profile.Pulses = Math.Clamp(profile.Pulses, 1, 255);
            profile.GapMilliseconds = Math.Clamp(profile.GapMilliseconds, 0, 65_535);
            profile.Palette = Math.Clamp(profile.Palette, 0, 3);
        }

        config.Ai.StateProfiles ??= new Dictionary<AiState, string>();
        foreach (var (state, profileId) in AiProfileIds)
        {
            if (!config.Ai.StateProfiles.TryGetValue(state, out var selected) ||
                string.IsNullOrWhiteSpace(selected) ||
                !config.Profiles.ContainsKey(selected))
            {
                config.Ai.StateProfiles[state] = profileId;
            }
        }

        if (string.IsNullOrWhiteSpace(config.SystemMonitor.ProfileId) ||
            !config.Profiles.ContainsKey(config.SystemMonitor.ProfileId))
        {
            config.SystemMonitor.ProfileId = "system-default";
        }

        if (string.IsNullOrWhiteSpace(config.Music.ProfileId) ||
            !config.Profiles.ContainsKey(config.Music.ProfileId))
        {
            config.Music.ProfileId = "music-default";
        }

        config.Touch.Contexts ??= new Dictionary<TouchContext, TouchBindings>();
        foreach (var (context, bindings) in CreateTouchContexts())
        {
            if (!config.Touch.Contexts.TryGetValue(context, out var current) || current is null)
            {
                config.Touch.Contexts[context] = bindings;
            }
        }

        return config;
    }

    private static Dictionary<TouchContext, TouchBindings> CreateTouchContexts() => new()
    {
        [TouchContext.Normal] = new TouchBindings
        {
            Click = TouchAction.NextPreset,
            DoubleClick = TouchAction.ToggleLights,
            LongPress = TouchAction.CyclePrimaryMode,
        },
        [TouchContext.Permission] = new TouchBindings
        {
            Click = TouchAction.AllowRequest,
            DoubleClick = TouchAction.DenyRequest,
            LongPress = TouchAction.DenyRequest,
        },
        [TouchContext.SystemMonitor] = new TouchBindings
        {
            Click = TouchAction.NextMetric,
            DoubleClick = TouchAction.NextVisualizer,
            LongPress = TouchAction.CyclePrimaryMode,
        },
        [TouchContext.Music] = new TouchBindings
        {
            Click = TouchAction.NextVisualizer,
            DoubleClick = TouchAction.NextTrack,
            LongPress = TouchAction.CyclePrimaryMode,
        },
    };

    private static IEnumerable<LightProfile> CreateProfiles()
    {
        yield return Profile("ai-idle", "AI 空闲", RgbEffect.Breathe, new(64, 120, 255), 32, 10);
        yield return Profile("ai-thinking", "AI 思考", RgbEffect.Rainbow, new(0, 255, 255), 56, 48);
        yield return Profile("ai-tools", "AI 执行", RgbEffect.Chase, new(255, 80, 255), 69, 90);
        yield return new LightProfile
        {
            Id = "ai-permission",
            Name = "等待授权",
            Effect = RgbEffect.Blink,
            Primary = new RgbColor(255, 200, 0),
            Brightness = 56,
            OnMilliseconds = 220,
            OffMilliseconds = 220,
            Pulses = 1,
            Gamma = true,
            MaxMilliamps = 200,
        };
        yield return Profile("ai-done", "AI 完成", RgbEffect.Solid, new(0, 255, 72), 56, 1);
        yield return Profile("ai-unknown", "AI 状态失联", RgbEffect.Breathe, new(128, 128, 128), 40, 40);
        yield return Profile("ai-error", "AI 错误", RgbEffect.Fire, new(255, 40, 0), 85, 140);
        yield return Profile("system-default", "系统负载", RgbEffect.Solid, new(0, 220, 90), 100, 1);
        yield return Profile("music-default", "音乐律动", RgbEffect.Palette, new(0, 180, 255), 100, 80);
    }

    private static LightProfile Profile(
        string id,
        string name,
        RgbEffect effect,
        RgbColor color,
        int brightness,
        int speed) =>
        new()
        {
            Id = id,
            Name = name,
            Effect = effect,
            Primary = color,
            Brightness = brightness,
            Speed = speed,
            Gamma = true,
            MaxMilliamps = 200,
        };

    private static RgbColor ClampColor(RgbColor color) => new(
        Math.Clamp(color.R, 0, 255),
        Math.Clamp(color.G, 0, 255),
        Math.Clamp(color.B, 0, 255));
}
