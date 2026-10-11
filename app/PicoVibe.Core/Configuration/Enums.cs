namespace PicoVibe.Core.Configuration;

/// <summary>The three mutually exclusive ambient operating modes.</summary>
public enum PrimaryMode
{
    Ai,
    SystemMonitor,
    Music,
}

/// <summary>Normalized AI lifecycle states shared by Cursor and Codex.</summary>
public enum AiState
{
    Idle,
    Thinking,
    Tools,
    Permission,
    Done,
    Error,
    Unknown,
}

public enum CursorShellGate
{
    Never,
    NonSandbox,
    All,
}

public enum CursorMcpGate
{
    Never,
    Mutating,
    All,
}

/// <summary>Firmware-native RGB.INI effects.</summary>
public enum RgbEffect
{
    Solid,
    Off,
    Blink,
    Rainbow,
    RainbowCycle,
    Chase,
    Comet,
    Theater,
    Wave,
    Scan,
    Sparkle,
    Twinkle,
    Breathe,
    Gradient,
    Fire,
    Palette,
}

public enum SystemMetric
{
    CpuUsage,
    MemoryUsage,
    CpuTemperature,
    GpuTemperature,
    DiskTemperature,
    BatteryLevel,
}

public enum SystemVisualizer
{
    Bar,
    GradientBar,
    Pulse,
    Comet,
    CenterBar,
    SegmentedBar,
    ColorWash,
    Breathing,
}

public enum MusicVisualizer
{
    VuMeter,
    Spectrum,
    CenterMirror,
    BassPulse,
    RainbowBeat,
    BandSplit,
    BeatChase,
    Wave,
    Aurora,
}

public enum TouchGesture
{
    None,
    Click,
    DoubleClick,
    LongPress,
}

public enum TouchContext
{
    Normal,
    Permission,
    SystemMonitor,
    Music,
}

public enum TouchAction
{
    None,
    NextPreset,
    PreviousPreset,
    ToggleLights,
    CyclePrimaryMode,
    ShowApplication,
    AllowRequest,
    DenyRequest,
    CancelRequest,
    NextMetric,
    NextVisualizer,
    TogglePause,
    NextTrack,
}
