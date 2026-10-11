using PicoVibe.Core.Lighting;

namespace PicoVibe.Core.Configuration;

public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GlobalSettings Global { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public SystemMonitorSettings SystemMonitor { get; set; } = new();
    public MusicSettings Music { get; set; } = new();
    public TouchSettings Touch { get; set; } = new();
    public Dictionary<string, LightProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppConfig CreateDefault() => AppConfigDefaults.Create();
}

public sealed class GlobalSettings
{
    public int BrightnessPercent { get; set; } = 50;
    public int LedCount { get; set; } = 12;
    public bool LightsEnabled { get; set; } = true;
    public bool LaunchAtLogin { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public PrimaryMode ActiveMode { get; set; } = PrimaryMode.Ai;
    public bool AlwaysShowAiPermission { get; set; } = true;

    /// <summary>
    /// Holds off idle system sleep while the lights are running. The board is
    /// powered by the card slot, and neither macOS nor Windows can re-identify
    /// it after a sleep without a physical re-plug, so leaving the lights on
    /// overnight is only safe with this on. Off by default: keeping a laptop
    /// awake is the user's call, not ours.
    /// </summary>
    public bool PreventSleepWhileLit { get; set; }
}

public sealed class AiSettings
{
    public AiDisplayMode DisplayMode { get; set; }
    public MultiAgentSettings MultiAgent { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public CursorShellGate CursorShellGate { get; set; } = CursorShellGate.Never;
    public CursorMcpGate CursorMcpGate { get; set; } = CursorMcpGate.Never;
    public bool GateFileTools { get; set; }
    public bool CodexTouchApproval { get; set; } = true;
    public int ApprovalTimeoutSeconds { get; set; } = 90;
    public int DoneOverlayMilliseconds { get; set; } = 2500;
    public int ErrorOverlayMilliseconds { get; set; } = 5000;
    public Dictionary<AiState, string> StateProfiles { get; set; } = new();
}

public sealed class SystemMonitorSettings
{
    public SystemMetric Metric { get; set; } = SystemMetric.CpuUsage;
    public string? SensorId { get; set; }
    public SystemVisualizer Visualizer { get; set; } = SystemVisualizer.GradientBar;
    public int UpdateIntervalMilliseconds { get; set; } = 250;
    public double DisplayMinimum { get; set; }
    public double DisplayMaximum { get; set; } = 100.0;
    public string ProfileId { get; set; } = "system-default";
}

public sealed class MusicSettings
{
    public string? EndpointId { get; set; }
    public MusicVisualizer Visualizer { get; set; } = MusicVisualizer.Spectrum;
    public string ProfileId { get; set; } = "music-default";
    public double Sensitivity { get; set; } = 1.0;
    public double NoiseGate { get; set; } = 0.025;
    public double Attack { get; set; } = 0.65;
    public double Release { get; set; } = 0.20;
    public int FramesPerSecond { get; set; } = 12;
}

public sealed class TouchSettings
{
    public bool Enabled { get; set; } = true;
    public Dictionary<TouchContext, TouchBindings> Contexts { get; set; } = new();
}

public sealed class TouchBindings
{
    public TouchAction Click { get; set; }
    public TouchAction DoubleClick { get; set; }
    public TouchAction LongPress { get; set; }

    public TouchAction For(TouchGesture gesture) => gesture switch
    {
        TouchGesture.Click => Click,
        TouchGesture.DoubleClick => DoubleClick,
        TouchGesture.LongPress => LongPress,
        _ => TouchAction.None,
    };
}

/// <summary>A reusable firmware-native light profile.</summary>
public sealed class LightProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RgbEffect Effect { get; set; } = RgbEffect.Solid;
    public RgbColor Primary { get; set; } = new(0, 160, 255);
    public RgbColor Secondary { get; set; } = new(255, 0, 160);
    public int Brightness { get; set; } = 64;
    public int Speed { get; set; } = 40;
    public bool Reverse { get; set; }
    public int Start { get; set; }
    public int Count { get; set; } = 12;
    public bool Gamma { get; set; } = true;
    public int MaxMilliamps { get; set; } = 200;
    public int OnMilliseconds { get; set; } = 150;
    public int OffMilliseconds { get; set; } = 850;
    public int Pulses { get; set; } = 1;
    public int GapMilliseconds { get; set; }
    public bool Fade { get; set; }
    public int Palette { get; set; }
}
