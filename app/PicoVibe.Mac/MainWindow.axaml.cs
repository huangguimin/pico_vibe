using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PicoVibe.App.Services;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Lighting;

namespace PicoVibe.Mac;

public partial class MainWindow : Window
{
    private readonly bool autostart;
    private readonly ConfigStore configStore = new();
    private readonly MacLaunchAtLoginService loginService = new();
    private readonly MacHookIntegrationService hookIntegration = new();
    private AppConfig config = AppConfig.CreateDefault();
    private AppConfig syncedConfig = AppConfig.CreateDefault();
    private LightOrchestrator? orchestrator;
    private CancellationTokenSource? saveDelay;
    private string? activeProfileId;
    private AiState activeAiState;
    private TouchContext activeTouchContext;
    private bool loading;
    private bool initialized;
    private bool profileEditorLoaded;
    private bool exiting;
    private bool lastLaunchAtLogin;

    public MainWindow() : this(false)
    {
    }

    public MainWindow(bool autostart)
    {
        this.autostart = autostart;
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    public event EventHandler? ExitRequested;

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (initialized) return;
        initialized = true;
        try
        {
            config = await LoadConfigWithRecoveryAsync();
            if (EnsureIndependentProfiles(config))
            {
                await configStore.SaveAsync(config);
            }
            syncedConfig = CloneConfig(config);
            PopulateOptions();
            LoadConfigIntoUi();
            WireEvents();
            lastLaunchAtLogin = config.Global.LaunchAtLogin;
            orchestrator = new LightOrchestrator(
                config,
                configStore,
                metricService: new MacSystemMetricService(),
                audioService: new MacAudioLevelService(),
                mediaController: new MacSystemMediaController());
            orchestrator.DeviceStatusChanged += OnDeviceStatusChanged;
            orchestrator.TouchDispatched += OnTouchDispatched;
            orchestrator.MetricChanged += OnMetricChanged;
            orchestrator.AudioLevelChanged += OnAudioLevelChanged;
            orchestrator.ShowRequested += OnShowRequested;
            orchestrator.ShutdownRequested += OnShutdownRequested;
            orchestrator.ConfigChanged += OnRuntimeConfigChanged;
            orchestrator.Start();
            StartAgentUiTimer();
            FooterText.Text = "配置会自动保存；macOS 系统音频首次使用时会请求权限。";
            if (autostart) Hide();
        }
        catch (Exception exception)
        {
            initialized = false;
            AppLog.Error("macOS application startup failed.", exception);
            ConnectionText.Text = "启动失败";
            FooterText.Text = $"启动失败：{exception.Message}";
        }
    }

    private async Task<AppConfig> LoadConfigWithRecoveryAsync()
    {
        try
        {
            return await configStore.LoadAsync();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            string backup = configStore.FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (File.Exists(configStore.FilePath)) File.Copy(configStore.FilePath, backup, overwrite: false);
            AppLog.Error($"Invalid configuration was preserved at {backup}.", exception);
            return AppConfig.CreateDefault();
        }
    }

    private void PopulateOptions()
    {
        PopulateAgentOptions();
        ModeCombo.ItemsSource = Enum.GetValues<PrimaryMode>();
        EffectCombo.ItemsSource = Enum.GetValues<RgbEffect>();
        ShellGateCombo.ItemsSource = Enum.GetValues<CursorShellGate>();
        McpGateCombo.ItemsSource = Enum.GetValues<CursorMcpGate>();
        AiStateCombo.ItemsSource = Enum.GetValues<AiState>();
        MetricCombo.ItemsSource = Enum.GetValues<SystemMetric>();
        SystemVisualizerCombo.ItemsSource = Enum.GetValues<SystemVisualizer>();
        MusicVisualizerCombo.ItemsSource = Enum.GetValues<MusicVisualizer>();
        TouchContextCombo.ItemsSource = Enum.GetValues<TouchContext>();
        TouchClickCombo.ItemsSource = Enum.GetValues<TouchAction>();
        TouchDoubleCombo.ItemsSource = Enum.GetValues<TouchAction>();
        TouchLongCombo.ItemsSource = Enum.GetValues<TouchAction>();
        RefreshProfileLists();
    }

    private void RefreshProfileLists()
    {
        string[] ids = config.Profiles.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        ProfileCombo.ItemsSource = ids;
        AiStateProfileCombo.ItemsSource = ids;
        SystemProfileCombo.ItemsSource = ids;
        MusicProfileCombo.ItemsSource = ids;
    }

    private void LoadConfigIntoUi()
    {
        var wasLoading = loading;
        loading = true;
        try
        {
            var keepSettingsOpen = NavigationList.SelectedIndex == 3;
            ModeCombo.SelectedItem = config.Global.ActiveMode;
            NavigationList.SelectedIndex = keepSettingsOpen
                ? 3
                : ModeIndex(config.Global.ActiveMode);
            LightsEnabledCheck.IsChecked = config.Global.LightsEnabled;
            GlobalBrightnessSlider.Value = config.Global.BrightnessPercent;
            GlobalBrightnessText.Text = $"{config.Global.BrightnessPercent}%";
            LaunchAtLoginCheck.IsChecked = config.Global.LaunchAtLogin;
            CloseToTrayCheck.IsChecked = config.Global.CloseToTray;
            AlwaysPermissionCheck.IsChecked = config.Global.AlwaysShowAiPermission;
            PreventSleepCheck.IsChecked = config.Global.PreventSleepWhileLit;
            LedCountNumber.Value = config.Global.LedCount;
            ApprovalTimeoutNumber.Value = config.Ai.ApprovalTimeoutSeconds;

            LoadAgentSettings();
            AiEnabledCheck.IsChecked = config.Ai.Enabled;
            CodexTouchCheck.IsChecked = config.Ai.CodexTouchApproval;
            GateFileToolsCheck.IsChecked = config.Ai.GateFileTools;
            ShellGateCombo.SelectedItem = config.Ai.CursorShellGate;
            McpGateCombo.SelectedItem = config.Ai.CursorMcpGate;
            DoneOverlayNumber.Value = config.Ai.DoneOverlayMilliseconds;
            ErrorOverlayNumber.Value = config.Ai.ErrorOverlayMilliseconds;
            AiStateCombo.SelectedItem = activeAiState;
            LoadAiStateMapping();

            MetricCombo.SelectedItem = config.SystemMonitor.Metric;
            SystemVisualizerCombo.SelectedItem = config.SystemMonitor.Visualizer;
            DisplayMinNumber.Value = (decimal)config.SystemMonitor.DisplayMinimum;
            DisplayMaxNumber.Value = (decimal)config.SystemMonitor.DisplayMaximum;
            MetricIntervalSlider.Value = config.SystemMonitor.UpdateIntervalMilliseconds;
            SystemProfileCombo.SelectedItem = config.SystemMonitor.ProfileId;

            MusicVisualizerCombo.SelectedItem = config.Music.Visualizer;
            MusicProfileCombo.SelectedItem = config.Music.ProfileId;
            SensitivitySlider.Value = config.Music.Sensitivity;
            NoiseGateSlider.Value = config.Music.NoiseGate;
            AttackSlider.Value = config.Music.Attack;
            ReleaseSlider.Value = config.Music.Release;
            FpsSlider.Value = config.Music.FramesPerSecond;

            TouchEnabledCheck.IsChecked = config.Touch.Enabled;
            TouchContextCombo.SelectedItem = activeTouchContext;
            LoadTouchContext();

            ShowSelectedPage(NavigationList.SelectedIndex);
            SyncProfileEditorToContext();
            UpdateValueLabels();
        }
        finally
        {
            loading = wasLoading;
        }
    }

    private void WireEvents()
    {
        WireAgentEvents();
        foreach (var combo in new[]
        {
            ModeCombo, EffectCombo, ShellGateCombo, McpGateCombo, AiStateProfileCombo,
            MetricCombo, SystemVisualizerCombo, SystemProfileCombo, MusicVisualizerCombo,
            MusicProfileCombo, TouchClickCombo, TouchDoubleCombo, TouchLongCombo,
        }) combo.SelectionChanged += OnAnySettingChanged;

        ProfileCombo.SelectionChanged += OnProfileSelectionChanged;
        AiStateCombo.SelectionChanged += OnAiStateChanged;
        TouchContextCombo.SelectionChanged += OnTouchContextChanged;
        NavigationList.SelectionChanged += OnNavigationChanged;
        MetricCombo.SelectionChanged += OnMetricSelectionChanged;

        ProfileNameText.TextChanged += OnAnySettingChanged;
        PrimaryColorPicker.ColorChanged += OnAnySettingChanged;
        SecondaryColorPicker.ColorChanged += OnAnySettingChanged;

        foreach (var number in new[]
        {
            LedCountNumber, ApprovalTimeoutNumber, StartNumber, CountNumber, OnMsNumber,
            OffMsNumber, PulsesNumber, GapMsNumber, MaxMaNumber, PaletteNumber,
            DoneOverlayNumber, ErrorOverlayNumber, DisplayMinNumber, DisplayMaxNumber,
        }) number.ValueChanged += OnAnySettingChanged;

        foreach (var check in new[]
        {
            LightsEnabledCheck, LaunchAtLoginCheck, CloseToTrayCheck, AlwaysPermissionCheck,
            PreventSleepCheck,
            ReverseCheck, GammaCheck, FadeCheck, AiEnabledCheck, CodexTouchCheck,
            GateFileToolsCheck, TouchEnabledCheck,
        }) check.IsCheckedChanged += OnAnySettingChanged;

        foreach (var slider in new[]
        {
            GlobalBrightnessSlider, ProfileBrightnessSlider, SpeedSlider, MetricIntervalSlider,
            SensitivitySlider, NoiseGateSlider, AttackSlider, ReleaseSlider, FpsSlider,
        }) slider.ValueChanged += OnAnySettingChanged;
        ReconnectButton.Click += async (_, _) =>
        {
            if (orchestrator is not null) await orchestrator.ForceReconnectAsync();
        };
        SaveButton.Click += async (_, _) => await SaveNowAsync();
        // The panel scrolls, so opening this at the bottom of a short window
        // would otherwise reveal the settings below the visible area.
        NativeAdvancedPanel.Expanded += (_, _) => Dispatcher.UIThread.Post(
            () => NativeAdvancedPanel.BringIntoView(), DispatcherPriority.Loaded);
        PreviewButton.Click += (_, _) =>
        {
            CaptureSelectedProfile();
            if (activeProfileId is not null && config.Profiles.TryGetValue(activeProfileId, out var profile))
                orchestrator?.Preview(profile);
        };
        OpenConfigButton.Click += (_, _) => OpenConfigFolder();
        VersionText.Text = DescribeVersion();
        // The LED preview paints its own brushes, so it has to be repainted
        // when macOS switches between light and dark.
        ActualThemeVariantChanged += (_, _) => UpdateLedPreview();
        InstallHooksButton.Click += async (_, _) =>
        {
            InstallHooksButton.IsEnabled = false;
            var result = await hookIntegration.InstallAsync();
            FooterText.Text = result.Success
                ? "AI 集成已安装。请在 Codex 中运行一次 /hooks 并信任 PicoVibeHook。"
                : $"AI 集成安装失败：{result.Message}";
            InstallHooksButton.IsEnabled = true;
        };
        UninstallHooksButton.Click += async (_, _) =>
        {
            UninstallHooksButton.IsEnabled = false;
            var result = await hookIntegration.UninstallAsync();
            FooterText.Text = result.Success ? "AI 集成已移除。" : $"移除失败：{result.Message}";
            UninstallHooksButton.IsEnabled = true;
        };
    }

    private void OnAnySettingChanged(object? sender, EventArgs e)
    {
        UpdateValueLabels();
        UpdateLedPreview();
        if (loading) return;
        ScheduleSave();
    }

    private void UpdateValueLabels()
    {
        GlobalBrightnessText.Text = $"{(int)Math.Round(GlobalBrightnessSlider.Value)}%";
        ProfileBrightnessValue.Text = $"{ProfileBrightnessSlider.Value / 255:P0}";
        SpeedValue.Text = $"{(int)Math.Round(SpeedSlider.Value)} / 255";
        MetricIntervalValue.Text = $"{(int)Math.Round(MetricIntervalSlider.Value)} ms";
        SensitivityValue.Text = $"{SensitivitySlider.Value:0.0}×";
        NoiseGateValue.Text = $"{NoiseGateSlider.Value * 100:0}%";
        AttackValue.Text = $"{AttackSlider.Value * 100:0}%";
        ReleaseValue.Text = $"{ReleaseSlider.Value * 100:0}%";
        FpsValue.Text = $"{(int)Math.Round(FpsSlider.Value)} FPS";
        PrimaryColorHex.Text = FormatHex(PrimaryColorPicker.Color);
        SecondaryColorHex.Text = FormatHex(SecondaryColorPicker.Color);
    }

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        CaptureSelectedProfile();
        activeProfileId = ProfileCombo.SelectedItem as string;
        LoadProfile();
        ScheduleSave();
    }

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading) return;

        CaptureSelectedProfile();
        var index = NavigationList.SelectedIndex;
        ShowSelectedPage(index);
        if (index is >= 0 and <= 2)
        {
            var mode = (PrimaryMode)index;
            config.Global.ActiveMode = mode;
            var wasLoading = loading;
            loading = true;
            try
            {
                ModeCombo.SelectedItem = mode;
            }
            finally
            {
                loading = wasLoading;
            }
            SyncProfileEditorToContext();
            ScheduleSave();
        }
    }

    private void OnAiStateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        CaptureSelectedProfile();
        CaptureAiStateMapping();
        if (AiStateCombo.SelectedItem is AiState state) activeAiState = state;
        LoadAiStateMapping();
        SyncProfileEditorToContext();
        ScheduleSave();
    }

    private void OnMetricSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || MetricCombo.SelectedItem is not SystemMetric metric) return;

        config.SystemMonitor.Metric = metric;
        var (minimum, maximum) = metric switch
        {
            SystemMetric.CpuTemperature or SystemMetric.GpuTemperature or SystemMetric.DiskTemperature => (30m, 100m),
            _ => (0m, 100m),
        };
        var wasLoading = loading;
        loading = true;
        try
        {
            DisplayMinNumber.Value = minimum;
            DisplayMaxNumber.Value = maximum;
            MetricValueText.Text = "--";
            MetricStatusText.Text = "正在读取…";
        }
        finally
        {
            loading = wasLoading;
        }
        ScheduleSave();
    }

    private void OnTouchContextChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        CaptureTouchContext();
        if (TouchContextCombo.SelectedItem is TouchContext context) activeTouchContext = context;
        LoadTouchContext();
        ScheduleSave();
    }

    private void LoadAiStateMapping()
    {
        var wasLoading = loading;
        loading = true;
        try
        {
            if (config.Ai.StateProfiles.TryGetValue(activeAiState, out string? profile))
                AiStateProfileCombo.SelectedItem = profile;
        }
        finally { loading = wasLoading; }
    }

    private void CaptureAiStateMapping()
    {
        if (AiStateProfileCombo.SelectedItem is string profile)
            config.Ai.StateProfiles[activeAiState] = profile;
    }

    private void LoadTouchContext()
    {
        var wasLoading = loading;
        loading = true;
        try
        {
            if (!config.Touch.Contexts.TryGetValue(activeTouchContext, out var bindings)) return;
            TouchClickCombo.SelectedItem = bindings.Click;
            TouchDoubleCombo.SelectedItem = bindings.DoubleClick;
            TouchLongCombo.SelectedItem = bindings.LongPress;
        }
        finally { loading = wasLoading; }
    }

    private void CaptureTouchContext()
    {
        if (!config.Touch.Contexts.TryGetValue(activeTouchContext, out var bindings)) return;
        if (TouchClickCombo.SelectedItem is TouchAction click) bindings.Click = click;
        if (TouchDoubleCombo.SelectedItem is TouchAction dbl) bindings.DoubleClick = dbl;
        if (TouchLongCombo.SelectedItem is TouchAction hold) bindings.LongPress = hold;
    }

    private void LoadProfile()
    {
        var wasLoading = loading;
        loading = true;
        try
        {
            if (activeProfileId is null || !config.Profiles.TryGetValue(activeProfileId, out var p)) return;
            profileEditorLoaded = true;
            ProfileNameText.Text = p.Name;
            EffectCombo.SelectedItem = p.Effect;
            PrimaryColorPicker.Color = ToPickerColor(p.Primary);
            SecondaryColorPicker.Color = ToPickerColor(p.Secondary);
            ProfileBrightnessSlider.Value = p.Brightness;
            SpeedSlider.Value = p.Speed;
            StartNumber.Value = p.Start;
            CountNumber.Value = p.Count;
            OnMsNumber.Value = p.OnMilliseconds;
            OffMsNumber.Value = p.OffMilliseconds;
            PulsesNumber.Value = p.Pulses;
            GapMsNumber.Value = p.GapMilliseconds;
            MaxMaNumber.Value = p.MaxMilliamps;
            PaletteNumber.Value = p.Palette;
            ReverseCheck.IsChecked = p.Reverse;
            GammaCheck.IsChecked = p.Gamma;
            FadeCheck.IsChecked = p.Fade;
            UpdateValueLabels();
            UpdateLedPreview();
        }
        finally { loading = wasLoading; }
    }

    private void CaptureSelectedProfile()
    {
        // Reading the editor before it has ever shown a profile would write its
        // defaults over the stored one: a white colour, brightness 0, no effect.
        if (!profileEditorLoaded) return;
        if (activeProfileId is null || !config.Profiles.TryGetValue(activeProfileId, out var p)) return;
        p.Name = ProfileNameText.Text?.Trim() ?? p.Name;
        if (EffectCombo.SelectedItem is RgbEffect effect) p.Effect = effect;
        p.Primary = FromPickerColor(PrimaryColorPicker.Color);
        p.Secondary = FromPickerColor(SecondaryColorPicker.Color);
        p.Brightness = (int)Math.Round(ProfileBrightnessSlider.Value);
        p.Speed = (int)Math.Round(SpeedSlider.Value);
        p.Start = NumberInt(StartNumber, p.Start);
        p.Count = NumberInt(CountNumber, p.Count);
        p.OnMilliseconds = NumberInt(OnMsNumber, p.OnMilliseconds);
        p.OffMilliseconds = NumberInt(OffMsNumber, p.OffMilliseconds);
        p.Pulses = NumberInt(PulsesNumber, p.Pulses);
        p.GapMilliseconds = NumberInt(GapMsNumber, p.GapMilliseconds);
        p.MaxMilliamps = NumberInt(MaxMaNumber, p.MaxMilliamps);
        p.Palette = NumberInt(PaletteNumber, p.Palette);
        p.Reverse = ReverseCheck.IsChecked == true;
        p.Gamma = GammaCheck.IsChecked == true;
        p.Fade = FadeCheck.IsChecked == true;
    }

    private void ShowSelectedPage(int index)
    {
        AiPage.IsVisible = index == 0;
        SystemPage.IsVisible = index == 1;
        MusicPage.IsVisible = index == 2;
        SettingsPage.IsVisible = index == 3;
        LightEditorPanel.IsVisible = index is >= 0 and <= 2;
        UpdateAgentModeVisibility();
    }

    private void SyncProfileEditorToContext()
    {
        string? profileId = config.Global.ActiveMode switch
        {
            PrimaryMode.Ai when config.Ai.StateProfiles.TryGetValue(activeAiState, out var aiId) => aiId,
            PrimaryMode.SystemMonitor => config.SystemMonitor.ProfileId,
            PrimaryMode.Music => config.Music.ProfileId,
            _ => null,
        };
        if (profileId is null || !config.Profiles.ContainsKey(profileId))
        {
            profileId = config.Profiles.Keys.FirstOrDefault();
        }

        var wasLoading = loading;
        loading = true;
        try
        {
            activeProfileId = profileId;
            ProfileCombo.SelectedItem = profileId;
            LightEditorTitle.Text = config.Global.ActiveMode switch
            {
                PrimaryMode.Ai => $"AI · {AiStateLabel(activeAiState)}",
                PrimaryMode.SystemMonitor => "系统状态",
                PrimaryMode.Music => "音乐律动",
                _ => "当前灯光",
            };
            var nativeEffect = config.Global.ActiveMode == PrimaryMode.Ai;
            NativeEffectPanel.IsVisible = nativeEffect;
            NativeAdvancedPanel.IsVisible = nativeEffect;
            PreviewButton.Content = nativeEffect
                ? "在灯带上预览"
                : "预览颜色与灯珠范围";
            LoadProfile();
        }
        finally
        {
            loading = wasLoading;
        }
    }

    private void UpdateLedPreview()
    {
        if (LedPreview is null) return;

        const int ledCount = 12;
        var start = Math.Clamp(NumberInt(StartNumber, 0), 0, ledCount - 1);
        var count = Math.Clamp(NumberInt(CountNumber, ledCount), 1, ledCount - start);
        var primary = FromPickerColor(PrimaryColorPicker.Color);
        var secondary = FromPickerColor(SecondaryColorPicker.Color);
        var scale = PreviewScale(ProfileBrightnessSlider.Value);
        var brushes = new List<IBrush>(ledCount);

        for (var led = 0; led < ledCount; led++)
        {
            if (led < start || led >= start + count)
            {
                brushes.Add(LedOffBrush());
                continue;
            }

            var local = led - start;
            var amount = count == 1 ? 0.0 : local / (double)(count - 1);
            byte Channel(int from, int to) => (byte)Math.Clamp(
                (int)Math.Round((from + ((to - from) * amount)) * scale),
                0,
                255);
            brushes.Add(new SolidColorBrush(Color.FromRgb(
                Channel(primary.R, secondary.R),
                Channel(primary.G, secondary.G),
                Channel(primary.B, secondary.B))));
        }

        LedPreview.ItemsSource = brushes;
    }

    /// <summary>
    /// The dots say which LEDs light up and in what colour; the percentage
    /// beside the slider already states the level. Scaling straight down to
    /// zero made every dot read as black below roughly a fifth of the range,
    /// which hid the colour exactly where people are checking it, so the
    /// preview keeps a floor and spends the rest of the range on the slider.
    /// </summary>
    private const double PreviewFloor = 0.5;

    private static double PreviewScale(double brightness) =>
        PreviewFloor + ((1 - PreviewFloor) * Math.Clamp(brightness / 255.0, 0, 1));

    private static int ModeIndex(PrimaryMode mode) => mode switch
    {
        PrimaryMode.Ai => 0,
        PrimaryMode.SystemMonitor => 1,
        PrimaryMode.Music => 2,
        _ => 0,
    };

    private static string AiStateLabel(AiState state) => state switch
    {
        AiState.Idle => "空闲",
        AiState.Thinking => "思考中",
        AiState.Tools => "执行工具",
        AiState.Permission => "等待确认",
        AiState.Done => "已完成",
        AiState.Error => "发生错误",
        _ => state.ToString(),
    };

    private static string MetricStatusLabel(SystemMetric metric, string source)
    {
        if (metric != SystemMetric.BatteryLevel)
        {
            return "数据实时更新";
        }

        return source switch
        {
            var value when value.Contains("Fully charged", StringComparison.OrdinalIgnoreCase) => "电池已充满",
            var value when value.Contains("Plugged in, discharging", StringComparison.OrdinalIgnoreCase) => "已接入电源，电池仍在放电",
            var value when value.Contains("Plugged in, not charging", StringComparison.OrdinalIgnoreCase) => "已接入电源，暂未充电",
            var value when value.Contains("Charging", StringComparison.OrdinalIgnoreCase) => "正在充电",
            var value when value.Contains("On battery", StringComparison.OrdinalIgnoreCase) => "正在使用电池",
            _ => "电池状态已更新",
        };
    }

    private static string AudioStatusLabel(string reason)
    {
        if (reason.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Screen Recording", StringComparison.OrdinalIgnoreCase))
        {
            return "需要系统音频权限。请在“系统设置 → 隐私与安全性”中允许 Pico Vibe，然后退出并重新打开应用。";
        }

        if (reason.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "切换到音乐律动后开始监听系统音频。";
        }

        return string.IsNullOrWhiteSpace(reason)
            ? "正在等待系统音频"
            : "暂时无法读取系统音频。";
    }

    private void CaptureUi()
    {
        CaptureAgentSettings();
        CaptureSelectedProfile();
        CaptureTouchContext();
        CaptureAiStateMapping();
        if (ModeCombo.SelectedItem is PrimaryMode mode) config.Global.ActiveMode = mode;
        config.Global.LightsEnabled = LightsEnabledCheck.IsChecked == true;
        config.Global.BrightnessPercent = (int)Math.Round(GlobalBrightnessSlider.Value);
        config.Global.LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true;
        config.Global.CloseToTray = CloseToTrayCheck.IsChecked == true;
        config.Global.AlwaysShowAiPermission = AlwaysPermissionCheck.IsChecked == true;
        config.Global.PreventSleepWhileLit = PreventSleepCheck.IsChecked == true;
        config.Global.LedCount = NumberInt(LedCountNumber, config.Global.LedCount);
        config.Ai.ApprovalTimeoutSeconds = NumberInt(ApprovalTimeoutNumber, config.Ai.ApprovalTimeoutSeconds);
        config.Ai.Enabled = AiEnabledCheck.IsChecked == true;
        config.Ai.CodexTouchApproval = CodexTouchCheck.IsChecked == true;
        config.Ai.GateFileTools = GateFileToolsCheck.IsChecked == true;
        if (ShellGateCombo.SelectedItem is CursorShellGate shell) config.Ai.CursorShellGate = shell;
        if (McpGateCombo.SelectedItem is CursorMcpGate mcp) config.Ai.CursorMcpGate = mcp;
        config.Ai.DoneOverlayMilliseconds = NumberInt(DoneOverlayNumber, config.Ai.DoneOverlayMilliseconds);
        config.Ai.ErrorOverlayMilliseconds = NumberInt(ErrorOverlayNumber, config.Ai.ErrorOverlayMilliseconds);

        if (MetricCombo.SelectedItem is SystemMetric metric) config.SystemMonitor.Metric = metric;
        if (SystemVisualizerCombo.SelectedItem is SystemVisualizer systemView)
            config.SystemMonitor.Visualizer = systemView;
        config.SystemMonitor.DisplayMinimum = NumberDouble(DisplayMinNumber, config.SystemMonitor.DisplayMinimum);
        config.SystemMonitor.DisplayMaximum = NumberDouble(DisplayMaxNumber, config.SystemMonitor.DisplayMaximum);
        config.SystemMonitor.UpdateIntervalMilliseconds = (int)Math.Round(MetricIntervalSlider.Value);
        if (SystemProfileCombo.SelectedItem is string systemProfile)
            config.SystemMonitor.ProfileId = systemProfile;

        if (MusicVisualizerCombo.SelectedItem is MusicVisualizer musicView)
            config.Music.Visualizer = musicView;
        if (MusicProfileCombo.SelectedItem is string musicProfile) config.Music.ProfileId = musicProfile;
        config.Music.Sensitivity = SensitivitySlider.Value;
        config.Music.NoiseGate = NoiseGateSlider.Value;
        config.Music.Attack = AttackSlider.Value;
        config.Music.Release = ReleaseSlider.Value;
        config.Music.FramesPerSecond = (int)Math.Round(FpsSlider.Value);
        config.Touch.Enabled = TouchEnabledCheck.IsChecked == true;
        AppConfigDefaults.Normalize(config);
    }

    private void ScheduleSave()
    {
        saveDelay?.Cancel();
        saveDelay?.Dispose();
        saveDelay = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(saveDelay.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(550, cancellationToken);
            await SaveNowAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task SaveNowAsync()
    {
        if (loading) return;
        try
        {
            CaptureUi();
            await configStore.SaveAsync(config);
            orchestrator?.UpdateConfig(config);
            syncedConfig = CloneConfig(config);
            if (lastLaunchAtLogin != config.Global.LaunchAtLogin)
            {
                bool applied = await loginService.SetEnabledAsync(config.Global.LaunchAtLogin);
                FooterText.Text = applied
                    ? "配置已保存，登录启动设置已更新。"
                    : "配置已保存；登录启动需要在正式 .app 中设置。";
                lastLaunchAtLogin = config.Global.LaunchAtLogin;
            }
            else
            {
                FooterText.Text = "配置已保存。";
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Saving macOS configuration failed.", exception);
            FooterText.Text = $"保存失败：{exception.Message}";
        }
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatus status) =>
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionText.Text = status.Message;
            // The device names its own firmware and board, which is what makes a
            // user's screenshot or log enough to tell which hardware they have.
            DeviceInfoText.Text = status.Connected && orchestrator is not null
                ? orchestrator.DeviceInfo.Describe()
                : "尚未连接设备";
        });

    private void OnTouchDispatched(object? sender, TouchDispatch dispatch) =>
        Dispatcher.UIThread.Post(() => LastTouchText.Text =
            $"{DateTime.Now:T}：{dispatch.Context} / {dispatch.Gesture} → {dispatch.Action}");

    private void OnMetricChanged(object? sender, MetricReading reading) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (reading.Metric != config.SystemMonitor.Metric) return;
            var sample = reading.Sample;
            var suffix = reading.Metric switch
            {
                SystemMetric.CpuUsage or SystemMetric.MemoryUsage or SystemMetric.BatteryLevel => "%",
                SystemMetric.CpuTemperature or SystemMetric.GpuTemperature or SystemMetric.DiskTemperature => " °C",
                _ => string.Empty,
            };
            MetricValueText.Text = sample.Available ? $"{sample.Value:0}{suffix}" : "--";
            MetricStatusText.Text = sample.Available
                ? MetricStatusLabel(reading.Metric, sample.Source)
                : $"暂不可用：{sample.Reason}";
        });

    private void OnAudioLevelChanged(object? sender, AudioLevelSample sample) =>
        Dispatcher.UIThread.Post(() =>
        {
            AudioLevelBar.Value = sample.Available ? sample.Rms : 0;
            AudioStatusText.Text = sample.Available
                ? "正在跟随系统音频"
                : AudioStatusLabel(sample.Reason);
        });

    private void OnShowRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ShowFromTray);

    private void OnShutdownRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RequestExit);

    private void OnRuntimeConfigChanged(object? sender, AppConfig updated) =>
        Dispatcher.UIThread.Post(() =>
        {
            CaptureUi();
            config = AppConfigMerge.PreserveLocalDraft(syncedConfig, config, updated);
            syncedConfig = CloneConfig(updated);
            LoadConfigIntoUi();
        });

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public async void RequestExit()
    {
        if (exiting) return;
        exiting = true;
        agentUiTimer?.Stop();
        saveDelay?.Cancel();
        await SaveNowAsync();
        if (orchestrator is not null)
        {
            await orchestrator.DisposeAsync();
            orchestrator = null;
        }
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (config.Global.CloseToTray)
        {
            Hide();
            FooterText.Text = "Pico Vibe 继续在菜单栏运行。";
        }
        else
        {
            RequestExit();
        }
    }

    /// <summary>
    /// Shows what is actually installed. The bundle carries both the release
    /// version and the build number, so two builds of the same version can be
    /// told apart; a plain `dotnet run` falls back to the assembly version.
    /// </summary>
    /// <summary>
    /// Colour for a lamp outside the selected range. It comes from the theme
    /// dictionary so the preview stays readable in dark mode.
    /// </summary>
    private IBrush LedOffBrush() =>
        this.TryFindResource("LedOffBrush", out var resource) && resource is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse("#E4E7EC"));

    private static string DescribeVersion() => $"Pico Vibe {ReadBundleVersion() ?? ReadAssemblyVersion()}";

    private static string? ReadBundleVersion()
    {
        try
        {
            var plist = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Info.plist"));
            if (!File.Exists(plist))
            {
                return null;
            }

            var entries = XDocument.Load(plist).Root?.Element("dict")?.Elements().ToList();
            if (entries is null)
            {
                return null;
            }

            string? Value(string name)
            {
                for (var index = 0; index < entries.Count - 1; index++)
                {
                    if (entries[index].Name == "key" &&
                        string.Equals(entries[index].Value, name, StringComparison.Ordinal))
                    {
                        return entries[index + 1].Value;
                    }
                }
                return null;
            }

            var release = Value("CFBundleShortVersionString");
            if (string.IsNullOrWhiteSpace(release))
            {
                return null;
            }
            var build = Value("CFBundleVersion");
            return string.IsNullOrWhiteSpace(build) ? release : $"{release}（构建 {build}）";
        }
        catch (Exception exception) when (exception is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadAssemblyVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "开发版";
        var metadata = text.IndexOf('+');
        return metadata > 0 ? text[..metadata] : text;
    }

    private void OpenConfigFolder()
    {
        string directory = Path.GetDirectoryName(configStore.FilePath)!;
        Directory.CreateDirectory(directory);
        try
        {
            Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "explorer.exe",
                $"\"{directory}\"") { UseShellExecute = false });
        }
        catch (Exception exception) { FooterText.Text = exception.Message; }
    }

    private static bool EnsureIndependentProfiles(AppConfig target)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        string Claim(string profileId, string ownerId)
        {
            if (claimed.Add(profileId) || !target.Profiles.TryGetValue(profileId, out var source))
            {
                return profileId;
            }

            var candidate = ownerId;
            for (var suffix = 2; target.Profiles.ContainsKey(candidate); suffix++)
            {
                candidate = $"{ownerId}-{suffix}";
            }

            target.Profiles[candidate] = CloneProfile(source, candidate);
            claimed.Add(candidate);
            changed = true;
            return candidate;
        }

        foreach (var state in Enum.GetValues<AiState>())
        {
            if (target.Ai.StateProfiles.TryGetValue(state, out var profileId))
            {
                target.Ai.StateProfiles[state] = Claim(
                    profileId,
                    $"ai-{state.ToString().ToLowerInvariant()}-independent");
            }
        }

        target.SystemMonitor.ProfileId = Claim(
            target.SystemMonitor.ProfileId,
            "system-independent");
        target.Music.ProfileId = Claim(
            target.Music.ProfileId,
            "music-independent");
        return changed;
    }

    private static LightProfile CloneProfile(LightProfile source, string id) => new()
    {
        Id = id,
        Name = source.Name,
        Effect = source.Effect,
        Primary = source.Primary,
        Secondary = source.Secondary,
        Brightness = source.Brightness,
        Speed = source.Speed,
        Reverse = source.Reverse,
        Start = source.Start,
        Count = source.Count,
        Gamma = source.Gamma,
        MaxMilliamps = source.MaxMilliamps,
        OnMilliseconds = source.OnMilliseconds,
        OffMilliseconds = source.OffMilliseconds,
        Pulses = source.Pulses,
        GapMilliseconds = source.GapMilliseconds,
        Fade = source.Fade,
        Palette = source.Palette,
    };

    private static AppConfig CloneConfig(AppConfig source) =>
        AppConfigMerge.PreserveLocalDraft(source, source, source);

    private static int NumberInt(NumericUpDown number, int fallback) =>
        number.Value is decimal value ? (int)Math.Round(value) : fallback;

    private static double NumberDouble(NumericUpDown number, double fallback) =>
        number.Value is decimal value ? (double)value : fallback;

    private static string FormatHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ToPickerColor(RgbColor color) =>
        Color.FromRgb((byte)color.R, (byte)color.G, (byte)color.B);

    private static RgbColor FromPickerColor(Color color) =>
        new(color.R, color.G, color.B);
}
