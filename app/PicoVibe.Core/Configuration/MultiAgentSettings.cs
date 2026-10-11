using PicoVibe.Core.Lighting;

namespace PicoVibe.Core.Configuration;

public enum AiDisplayMode { Overall, PerAgent }
public enum AgentLightEffect { Solid, Breathe, Blink }
public enum AgentLightState { Idle, Thinking, Tools, Permission, NativePermission, Done, Error, Unknown }

public sealed class AgentLightProfile
{
    public RgbColor Color { get; set; } = new(0, 100, 255);
    public AgentLightEffect Effect { get; set; }
    public int BrightnessPercent { get; set; } = 45;
    public int PeriodMilliseconds { get; set; } = 2400;
    public AgentLightProfile Clone() => (AgentLightProfile)MemberwiseClone();
}

public sealed class MultiAgentSettings
{
    public bool Reverse { get; set; }
    public Dictionary<string, string> AgentNames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<AgentLightState, AgentLightProfile> Profiles { get; set; } = Defaults();

    public MultiAgentSettings Clone() => new()
    {
        Reverse = Reverse,
        AgentNames = new(AgentNames, StringComparer.Ordinal),
        Profiles = Profiles.ToDictionary(p => p.Key, p => p.Value.Clone()),
    };

    public static MultiAgentSettings Normalize(MultiAgentSettings? settings)
    {
        settings ??= new();
        settings.AgentNames ??= new(StringComparer.Ordinal);
        settings.Profiles ??= new();
        foreach (var (state, profile) in Defaults())
        {
            if (!settings.Profiles.TryGetValue(state, out var existing) || existing is null)
                settings.Profiles[state] = existing = profile;
            existing.BrightnessPercent = Math.Clamp(existing.BrightnessPercent, 0, 100);
            existing.PeriodMilliseconds = Math.Clamp(existing.PeriodMilliseconds, 400, 10000);
            existing.Color = new(Math.Clamp(existing.Color.R, 0, 255), Math.Clamp(existing.Color.G, 0, 255), Math.Clamp(existing.Color.B, 0, 255));
            if (!Enum.IsDefined(existing.Effect)) existing.Effect = AgentLightEffect.Solid;
        }
        return settings;
    }

    private static Dictionary<AgentLightState, AgentLightProfile> Defaults() => new()
    {
        [AgentLightState.Idle] = P(new(0, 45, 110), AgentLightEffect.Solid, 15),
        [AgentLightState.Thinking] = P(new(0, 120, 255), AgentLightEffect.Breathe),
        [AgentLightState.Tools] = P(new(155, 65, 255), AgentLightEffect.Blink, period: 800),
        [AgentLightState.Permission] = P(new(255, 200, 0), AgentLightEffect.Blink, period: 1000),
        [AgentLightState.NativePermission] = P(new(255, 200, 0), AgentLightEffect.Solid),
        [AgentLightState.Done] = P(new(0, 255, 72), AgentLightEffect.Solid),
        [AgentLightState.Error] = P(new(255, 35, 0), AgentLightEffect.Blink, period: 600),
        [AgentLightState.Unknown] = P(new(128, 128, 128), AgentLightEffect.Breathe, period: 4000),
    };

    private static AgentLightProfile P(RgbColor color, AgentLightEffect effect, int brightness = 45, int period = 2400) =>
        new() { Color = color, Effect = effect, BrightnessPercent = brightness, PeriodMilliseconds = period };
}
