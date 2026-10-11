using PicoVibe.Core.Configuration;
using PicoVibe.Core.Device;

namespace PicoVibe.Core.Lighting;

public static class AgentFrameRenderer
{
    public static AgentLightState StateFor(AgentLamp lamp) => lamp.TouchReady ? AgentLightState.Permission
        : lamp.Agent.State == AiState.Permission ? AgentLightState.NativePermission
        : Enum.Parse<AgentLightState>(lamp.Agent.State.ToString());

    public static int PhysicalSlot(int slot, int capacity, bool reverse) => reverse ? capacity - 1 - slot : slot;

    public static byte[] Render(IReadOnlyList<AgentLamp> lamps, MultiAgentSettings settings, GlobalSettings global,
        DateTimeOffset now, int capacity, string? locateKey = null)
    {
        var frame = new byte[VibePacketEncoder.RgbBytes];
        if (!global.LightsEnabled) return frame;
        foreach (var lamp in lamps)
        {
            if (lamp.Slot is not { } slot || slot >= capacity) continue;
            var profile = settings.Profiles[StateFor(lamp)];
            var phase = now.ToUnixTimeMilliseconds() % profile.PeriodMilliseconds / (double)profile.PeriodMilliseconds;
            var factor = profile.Effect switch
            {
                AgentLightEffect.Breathe => 0.08 + 0.92 * (1 - Math.Cos(phase * Math.PI * 2)) / 2,
                AgentLightEffect.Blink => phase < 0.5 ? 1.0 : 0.0,
                _ => 1.0,
            };
            var color = profile.Color;
            if (lamp.Agent.Key == locateKey && !lamps.Any(l => l.TouchReady || l.Agent.State == AiState.Permission))
            { color = RgbColor.White; factor = 1; }
            var brightness = Math.Min(profile.BrightnessPercent, global.BrightnessPercent) / 100.0;
            var offset = PhysicalSlot(slot, capacity, settings.Reverse) * 3;
            frame[offset] = (byte)Math.Clamp(Math.Round(color.R * brightness * factor), 0, 255);
            frame[offset + 1] = (byte)Math.Clamp(Math.Round(color.G * brightness * factor), 0, 255);
            frame[offset + 2] = (byte)Math.Clamp(Math.Round(color.B * brightness * factor), 0, 255);
        }
        return frame;
    }
}
