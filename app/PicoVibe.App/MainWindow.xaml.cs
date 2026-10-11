using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PicoVibe.App.Services;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Lighting;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PicoVibe.App;

public partial class MainWindow : Window
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "PicoVibe";

    private readonly bool autostart;
    private readonly ConfigStore configStore = new();
    private readonly DispatcherTimer saveTimer;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private AppConfig config = AppConfig.CreateDefault();
    private AppConfig syncedConfig = AppConfig.CreateDefault();
    private LightOrchestrator? orchestrator;
    private Forms.NotifyIcon? trayIcon;
    private Drawing.Icon? trayDrawingIcon;
    private bool uiReady;
    private bool exiting;
    private bool loadingAiProfile;
    private bool syncingMode;
    private bool uiDirty;
    private long uiRevision;
    private string? startupNotice;
    private AiState editingAiState = AiState.Idle;

    public MainWindow(bool autostart = false)
    {
        this.autostart = autostart;
        InitializeComponent();
        saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(550), DispatcherPriority.Background, OnSaveTimer, Dispatcher)
        {
            IsEnabled = false,
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            config = await LoadConfigWithRecoveryAsync();
            syncedConfig = CloneConfig(config);
            PopulateOptions();
            LoadConfigIntoUi();
            AttachLiveHandlers();
            SetupTrayIcon();
            orchestrator = new LightOrchestrator(
                config,
                configStore,
                metricService: new SystemMetricService(),
                audioService: new AudioLevelService(),
                mediaController: new WindowsSystemMediaController());
            orchestrator.DeviceStatusChanged += OnDeviceStatusChanged;
            orchestrator.TouchDispatched += OnTouchDispatched;
            orchestrator.MetricChanged += OnMetricChanged;
            orchestrator.AudioLevelChanged += OnAudioLevelChanged;
            orchestrator.ShowRequested += OnShowRequested;
            orchestrator.ShutdownRequested += OnShutdownRequested;
            orchestrator.ConfigChanged += OnOrchestratorConfigChanged;
            orchestrator.Start();
            uiReady = true;
            FooterStatusText.Text = startupNotice ?? "配置会自动保存";

            if (autostart)
            {
                _ = Dispatcher.BeginInvoke(Hide, DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Application startup failed.", exception);
            ConnectionDot.Fill = Brushes.IndianRed;
            ConnectionText.Text = "启动失败";
            FooterStatusText.Text = $"启动失败：{exception.Message}";
        }
    }

    private async Task<AppConfig> LoadConfigWithRecoveryAsync()
    {
        try
        {
            return await configStore.LoadAsync();
        }
        catch (JsonException exception) when (File.Exists(configStore.FilePath))
        {
            var backup = $"{configStore.FilePath}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(configStore.FilePath, backup, overwrite: false);
            AppLog.Error($"Invalid configuration was preserved at {backup}; defaults were loaded.", exception);
            startupNotice = $"配置损坏，已备份为 {Path.GetFileName(backup)} 并恢复默认值";
            return AppConfig.CreateDefault();
        }
    }

    private void PopulateOptions()
    {
        SetOptions(AiStateCombo,
            (AiState.Idle, "空闲"),
            (AiState.Thinking, "思考中"),
            (AiState.Tools, "运行 / 工具"),
            (AiState.Permission, "等待授权"),
            (AiState.Done, "完成"),
            (AiState.Error, "错误"),
            (AiState.Unknown, "状态失联"));
        SetOptions(EffectModeCombo, Enum.GetValues<RgbEffect>().Select(value =>
            (value, value switch
            {
                RgbEffect.Solid => "常亮",
                RgbEffect.Off => "关闭",
                RgbEffect.Blink => "闪烁",
                RgbEffect.Rainbow => "彩虹",
                RgbEffect.RainbowCycle => "彩虹循环",
                RgbEffect.Chase => "追逐",
                RgbEffect.Comet => "彗星",
                RgbEffect.Theater => "剧院灯",
                RgbEffect.Wave => "波浪",
                RgbEffect.Scan => "扫描",
                RgbEffect.Sparkle => "闪花",
                RgbEffect.Twinkle => "星光",
                RgbEffect.Breathe => "呼吸",
                RgbEffect.Gradient => "渐变",
                RgbEffect.Fire => "火焰",
                RgbEffect.Palette => "调色板",
                _ => value.ToString(),
            })).ToArray());
        SetOptions(PaletteCombo,
            (PaletteChoice.Rainbow, "彩虹"),
            (PaletteChoice.Warm, "暖色"),
            (PaletteChoice.Ice, "冰色"),
            (PaletteChoice.Neon, "霓虹"));
        SetOptions(ShellGateCombo,
            (CursorShellGate.Never, "从不额外拦截"),
            (CursorShellGate.NonSandbox, "仅非沙箱命令"),
            (CursorShellGate.All, "所有命令"));
        SetOptions(McpGateCombo,
            (CursorMcpGate.Never, "从不额外拦截"),
            (CursorMcpGate.Mutating, "仅可能修改数据"),
            (CursorMcpGate.All, "所有 MCP 调用"));
        SetOptions(SystemMetricCombo,
            (SystemMetric.CpuUsage, "CPU 使用率"),
            (SystemMetric.MemoryUsage, "内存使用率"),
            (SystemMetric.BatteryLevel, "电池电量"),
            (SystemMetric.CpuTemperature, "CPU 温度"),
            (SystemMetric.GpuTemperature, "GPU 温度"),
            (SystemMetric.DiskTemperature, "硬盘温度"));
        SetOptions(SystemVisualizerCombo,
            (SystemVisualizer.Bar, "单色进度条"),
            (SystemVisualizer.GradientBar, "渐变进度条"),
            (SystemVisualizer.Pulse, "整体亮度"),
            (SystemVisualizer.Comet, "彗星指针"),
            (SystemVisualizer.CenterBar, "中心扩展"),
            (SystemVisualizer.SegmentedBar, "分段仪表"),
            (SystemVisualizer.ColorWash, "状态染色"),
            (SystemVisualizer.Breathing, "呼吸状态"));
        SetOptions(MusicVisualizerCombo,
            (MusicVisualizer.VuMeter, "VU 电平"),
            (MusicVisualizer.Spectrum, "三段电平"),
            (MusicVisualizer.CenterMirror, "中心镜像"),
            (MusicVisualizer.BassPulse, "节拍呼吸"),
            (MusicVisualizer.RainbowBeat, "彩虹节拍"),
            (MusicVisualizer.BandSplit, "分区律动"),
            (MusicVisualizer.BeatChase, "节拍追逐"),
            (MusicVisualizer.Wave, "流动声浪"),
            (MusicVisualizer.Aurora, "极光流动"));

        var common = new[]
        {
            TouchAction.None,
            TouchAction.ToggleLights,
            TouchAction.CyclePrimaryMode,
            TouchAction.ShowApplication,
        };
        SetTouchOptions(
            new[] { NormalClickCombo, NormalDoubleCombo, NormalLongCombo },
            common.Concat(new[] { TouchAction.NextPreset, TouchAction.PreviousPreset }));
        SetTouchOptions(
            new[] { PermissionClickCombo, PermissionDoubleCombo, PermissionLongCombo },
            new[] { TouchAction.None, TouchAction.AllowRequest, TouchAction.DenyRequest, TouchAction.CancelRequest });
        SetTouchOptions(
            new[] { SystemClickCombo, SystemDoubleCombo, SystemLongCombo },
            common.Concat(new[] { TouchAction.NextMetric, TouchAction.NextVisualizer }));
        SetTouchOptions(
            new[] { MusicClickCombo, MusicDoubleCombo, MusicLongCombo },
            common.Concat(new[] { TouchAction.NextVisualizer, TouchAction.TogglePause, TouchAction.NextTrack }));
    }

    private void LoadConfigIntoUi()
    {
        uiReady = false;
        GlobalBrightnessSlider.Value = config.Global.BrightnessPercent;
        GlobalBrightnessText.Text = $"{config.Global.BrightnessPercent}%";
        LightsEnabledCheck.IsChecked = config.Global.LightsEnabled;
        AiModeButton.IsChecked = config.Global.ActiveMode == PrimaryMode.Ai;
        SystemModeButton.IsChecked = config.Global.ActiveMode == PrimaryMode.SystemMonitor;
        MusicModeButton.IsChecked = config.Global.ActiveMode == PrimaryMode.Music;
        MainTabs.SelectedIndex = (int)config.Global.ActiveMode;

        Select(AiStateCombo, editingAiState);
        AiEnabledCheck.IsChecked = config.Ai.Enabled;
        Select(ShellGateCombo, config.Ai.CursorShellGate);
        Select(McpGateCombo, config.Ai.CursorMcpGate);
        FileToolGateCheck.IsChecked = config.Ai.GateFileTools;
        CodexTouchCheck.IsChecked = config.Ai.CodexTouchApproval;
        AmbientAskOverrideCheck.IsChecked = config.Global.AlwaysShowAiPermission;
        ApprovalTimeoutText.Text = config.Ai.ApprovalTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        LoadAiProfile(editingAiState);

        Select(SystemMetricCombo, config.SystemMonitor.Metric);
        Select(SystemVisualizerCombo, config.SystemMonitor.Visualizer);
        var systemProfile = GetProfile(config.SystemMonitor.ProfileId);
        SetColor(SystemLowColorButton, systemProfile.Primary);
        SetColor(SystemHighColorButton, systemProfile.Secondary);
        SystemMinText.Text = config.SystemMonitor.DisplayMinimum.ToString("0.##", CultureInfo.CurrentCulture);
        SystemMaxText.Text = config.SystemMonitor.DisplayMaximum.ToString("0.##", CultureInfo.CurrentCulture);
        SystemIntervalSlider.Value = config.SystemMonitor.UpdateIntervalMilliseconds;
        SystemIntervalText.Text = $"{config.SystemMonitor.UpdateIntervalMilliseconds} ms";

        Select(MusicVisualizerCombo, config.Music.Visualizer);
        var musicProfile = GetProfile(config.Music.ProfileId);
        SetColor(MusicPrimaryColorButton, musicProfile.Primary);
        SetColor(MusicSecondaryColorButton, musicProfile.Secondary);
        MusicSensitivitySlider.Value = config.Music.Sensitivity;
        MusicNoiseGateSlider.Value = config.Music.NoiseGate;
        MusicSmoothingSlider.Value = config.Music.Release;
        MusicFpsSlider.Value = config.Music.FramesPerSecond;
        UpdateMusicLabels();

        LoadTouchBindings(TouchContext.Normal, NormalClickCombo, NormalDoubleCombo, NormalLongCombo);
        LoadTouchBindings(TouchContext.Permission, PermissionClickCombo, PermissionDoubleCombo, PermissionLongCombo);
        LoadTouchBindings(TouchContext.SystemMonitor, SystemClickCombo, SystemDoubleCombo, SystemLongCombo);
        LoadTouchBindings(TouchContext.Music, MusicClickCombo, MusicDoubleCombo, MusicLongCombo);
        TouchEnabledCheck.IsChecked = config.Touch.Enabled;
        StartWithWindowsCheck.IsChecked = config.Global.LaunchAtLogin;
        CloseToTrayCheck.IsChecked = config.Global.CloseToTray;
        PreventSleepCheck.IsChecked = config.Global.PreventSleepWhileLit;
        VersionText.Text = DescribeVersion();
        uiReady = true;
    }

    private void AttachLiveHandlers()
    {
        ProfileBrightnessSlider.ValueChanged += (_, _) =>
        {
            ProfileBrightnessText.Text = $"{(int)ProfileBrightnessSlider.Value} / 255";
            ScheduleSave();
        };
        EffectSpeedSlider.ValueChanged += (_, _) =>
        {
            EffectSpeedText.Text = ((int)EffectSpeedSlider.Value).ToString(CultureInfo.CurrentCulture);
            ScheduleSave();
        };
        SystemIntervalSlider.ValueChanged += (_, _) =>
        {
            SystemIntervalText.Text = $"{(int)SystemIntervalSlider.Value} ms";
            ScheduleSave();
        };
        MusicSensitivitySlider.ValueChanged += OnMusicSliderChanged;
        MusicNoiseGateSlider.ValueChanged += OnMusicSliderChanged;
        MusicSmoothingSlider.ValueChanged += OnMusicSliderChanged;
        MusicFpsSlider.ValueChanged += OnMusicSliderChanged;
        LightsEnabledCheck.Checked += OnGenericSettingChanged;
        LightsEnabledCheck.Unchecked += OnGenericSettingChanged;
        AiEnabledCheck.Checked += OnGenericSettingChanged;
        AiEnabledCheck.Unchecked += OnGenericSettingChanged;
        FileToolGateCheck.Checked += OnGenericSettingChanged;
        FileToolGateCheck.Unchecked += OnGenericSettingChanged;
        CodexTouchCheck.Checked += OnGenericSettingChanged;
        CodexTouchCheck.Unchecked += OnGenericSettingChanged;
        AmbientAskOverrideCheck.Checked += OnGenericSettingChanged;
        AmbientAskOverrideCheck.Unchecked += OnGenericSettingChanged;
        StartWithWindowsCheck.Checked += OnGenericSettingChanged;
        StartWithWindowsCheck.Unchecked += OnGenericSettingChanged;
        CloseToTrayCheck.Checked += OnGenericSettingChanged;
        CloseToTrayCheck.Unchecked += OnGenericSettingChanged;
        PreventSleepCheck.Checked += OnGenericSettingChanged;
        PreventSleepCheck.Unchecked += OnGenericSettingChanged;
        TouchEnabledCheck.Checked += OnGenericSettingChanged;
        TouchEnabledCheck.Unchecked += OnGenericSettingChanged;
        ShellGateCombo.SelectionChanged += OnGenericSettingChanged;
        McpGateCombo.SelectionChanged += OnGenericSettingChanged;
        EffectModeCombo.SelectionChanged += OnGenericSettingChanged;
        PaletteCombo.SelectionChanged += OnGenericSettingChanged;
        foreach (var combo in TouchCombos())
        {
            combo.SelectionChanged += OnGenericSettingChanged;
        }
        SystemMinText.LostFocus += OnGenericSettingChanged;
        SystemMaxText.LostFocus += OnGenericSettingChanged;
        ApprovalTimeoutText.LostFocus += OnGenericSettingChanged;
        foreach (var textBox in new[]
        {
            StartLedText,
            LedCountText,
            BlinkOnText,
            BlinkOffText,
            BlinkPulsesText,
            BlinkGapText,
            MaxMaText,
        })
        {
            textBox.LostFocus += OnGenericSettingChanged;
        }
        foreach (var checkBox in new[] { ReverseCheck, GammaCheck, FadeCheck })
        {
            checkBox.Checked += OnGenericSettingChanged;
            checkBox.Unchecked += OnGenericSettingChanged;
        }
    }

    private void ModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (MainTabs is null || syncingMode)
        {
            return;
        }
        var mode = sender == SystemModeButton
            ? PrimaryMode.SystemMonitor
            : sender == MusicModeButton
                ? PrimaryMode.Music
                : PrimaryMode.Ai;
        syncingMode = true;
        try
        {
            MainTabs.SelectedIndex = (int)mode;
        }
        finally
        {
            syncingMode = false;
        }
        if (uiReady)
        {
            config.Global.ActiveMode = mode;
            SettingsButton.FontWeight = FontWeights.Normal;
            ScheduleSave();
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingMode || !ReferenceEquals(e.Source, MainTabs) || MainTabs.SelectedIndex is < 0 or > 2)
        {
            return;
        }

        var mode = (PrimaryMode)MainTabs.SelectedIndex;
        syncingMode = true;
        try
        {
            AiModeButton.IsChecked = mode == PrimaryMode.Ai;
            SystemModeButton.IsChecked = mode == PrimaryMode.SystemMonitor;
            MusicModeButton.IsChecked = mode == PrimaryMode.Music;
        }
        finally
        {
            syncingMode = false;
        }

        if (uiReady)
        {
            config.Global.ActiveMode = mode;
            SettingsButton.FontWeight = FontWeights.Normal;
            ScheduleSave();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 3;
        SettingsButton.FontWeight = FontWeights.SemiBold;
    }

    private void GlobalBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlobalBrightnessText is null)
        {
            return;
        }
        GlobalBrightnessText.Text = $"{(int)e.NewValue}%";
        ScheduleSave();
    }

    private void AiStateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!uiReady || Selected<AiState>(AiStateCombo) is not { } selected)
        {
            return;
        }
        SaveCurrentAiProfile();
        editingAiState = selected;
        LoadAiProfile(selected);
        ScheduleSave();
    }

    private void LoadAiProfile(AiState state)
    {
        if (!config.Ai.StateProfiles.TryGetValue(state, out var profileId))
        {
            return;
        }
        loadingAiProfile = true;
        try
        {
            var profile = GetProfile(profileId);
            Select(EffectModeCombo, profile.Effect);
            Select(PaletteCombo, (PaletteChoice)profile.Palette);
            SetColor(PrimaryColorButton, profile.Primary);
            SetColor(SecondaryColorButton, profile.Secondary);
            ProfileBrightnessSlider.Value = profile.Brightness;
            EffectSpeedSlider.Value = profile.Speed;
            StartLedText.Text = profile.Start.ToString(CultureInfo.CurrentCulture);
            LedCountText.Text = profile.Count.ToString(CultureInfo.CurrentCulture);
            BlinkOnText.Text = profile.OnMilliseconds.ToString(CultureInfo.CurrentCulture);
            BlinkOffText.Text = profile.OffMilliseconds.ToString(CultureInfo.CurrentCulture);
            BlinkPulsesText.Text = profile.Pulses.ToString(CultureInfo.CurrentCulture);
            BlinkGapText.Text = profile.GapMilliseconds.ToString(CultureInfo.CurrentCulture);
            ReverseCheck.IsChecked = profile.Reverse;
            GammaCheck.IsChecked = profile.Gamma;
            FadeCheck.IsChecked = profile.Fade;
            MaxMaText.Text = profile.MaxMilliamps.ToString(CultureInfo.CurrentCulture);
            ProfileBrightnessText.Text = $"{profile.Brightness} / 255";
            EffectSpeedText.Text = profile.Speed.ToString(CultureInfo.CurrentCulture);
        }
        finally
        {
            loadingAiProfile = false;
        }
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        var current = GetColor(button);
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            SetColor(button, new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B));
            ScheduleSave();
        }
    }

    private async void SaveAiProfile_Click(object sender, RoutedEventArgs e)
    {
        saveTimer.Stop();
        AiSaveHint.Text = "正在保存…";
        AiSaveHint.Text = await SaveConfigurationAsync()
            ? "已保存"
            : "保存失败，请查看页脚";
    }

    private void PreviewAiProfile_Click(object sender, RoutedEventArgs e)
    {
        var preview = ReadCurrentAiProfile(clone: true);
        orchestrator?.Preview(preview);
        AiSaveHint.Text = "正在预览 3 秒";
    }

    private void SystemSetting_Changed(object sender, SelectionChangedEventArgs e) => ScheduleSave();

    private void MusicSetting_Changed(object sender, SelectionChangedEventArgs e) => ScheduleSave();

    private void OnMusicSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateMusicLabels();
        ScheduleSave();
    }

    private void UpdateMusicLabels()
    {
        MusicSensitivityText.Text = $"{MusicSensitivitySlider.Value:0.0}×";
        MusicNoiseGateText.Text = $"{MusicNoiseGateSlider.Value * 100:0.0}%";
        MusicSmoothingText.Text = $"{MusicSmoothingSlider.Value * 100:0}%";
        MusicFpsText.Text = $"{(int)MusicFpsSlider.Value} FPS";
    }

    /// <summary>Shows which build is actually installed.</summary>
    private static string DescribeVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "开发版";
        var metadata = text.IndexOf('+');
        return $"Pico Vibe {(metadata > 0 ? text[..metadata] : text)}";
    }

    private void OnGenericSettingChanged(object sender, RoutedEventArgs e) => ScheduleSave();

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ConnectionText.Text = "正在重新检测设备";
        if (orchestrator is not null)
        {
            await orchestrator.ForceReconnectAsync();
        }
    }

    private async void OpenCodexHookReview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FooterStatusText.Text = "正在准备 Codex Hook 审核…";
            await CodexHookReviewLauncher.InstallHooksAndOpenAsync(AppContext.BaseDirectory);
            FooterStatusText.Text = "Codex 已打开；若未自动提示，请输入 /hooks";
            System.Windows.MessageBox.Show(
                "已重新安装 Pico Vibe Hook 并打开 Codex。\n\n" +
                "如果没有自动出现 Hooks need review，请在 Codex 中输入 /hooks，" +
                "检查 Pico Vibe 条目后选择信任。\n\n" +
                "Pico Vibe 不会替你自动授予信任。",
                "Pico Vibe · Codex Hook 审核",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppLog.Error("Opening Codex hook review failed.", exception);
            FooterStatusText.Text = $"打开 Codex Hook 审核失败：{exception.Message}";
            System.Windows.MessageBox.Show(
                exception.Message,
                "无法打开 Codex Hook 审核",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(configStore.FilePath)!;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await SaveConfigurationAsync();

    private void ScheduleSave(bool immediate = false)
    {
        if (!uiReady || loadingAiProfile || exiting)
        {
            return;
        }
        uiDirty = true;
        uiRevision++;
        saveTimer.Stop();
        saveTimer.Interval = immediate ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromMilliseconds(550);
        saveTimer.Start();
        FooterStatusText.Text = "有配置等待保存…";
    }

    private async void OnSaveTimer(object? sender, EventArgs e)
    {
        saveTimer.Stop();
        await SaveConfigurationAsync();
    }

    private async Task<bool> SaveConfigurationAsync(bool allowDuringExit = false)
    {
        if (!uiReady || (exiting && !allowDuringExit))
        {
            return false;
        }
        saveTimer.Stop();
        await saveGate.WaitAsync();
        try
        {
            if (!uiReady || (exiting && !allowDuringExit))
            {
                return false;
            }
            var savedRevision = uiRevision;
            ReadUiIntoConfig();
            var snapshot = CloneConfig(config);
            orchestrator?.UpdateConfig(snapshot);
            syncedConfig = CloneConfig(snapshot);
            await configStore.SaveAsync(snapshot);
            UpdateStartupRegistration(snapshot.Global.LaunchAtLogin);
            if (uiRevision == savedRevision)
            {
                uiDirty = false;
            }
            FooterStatusText.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("Saving configuration failed.", exception);
            FooterStatusText.Text = $"保存失败：{exception.Message}";
            return false;
        }
        finally
        {
            saveGate.Release();
        }
    }

    private void ReadUiIntoConfig()
    {
        SaveCurrentAiProfile();
        config.Global.BrightnessPercent = (int)GlobalBrightnessSlider.Value;
        config.Global.LightsEnabled = LightsEnabledCheck.IsChecked == true;
        config.Global.AlwaysShowAiPermission = AmbientAskOverrideCheck.IsChecked == true;
        config.Global.LaunchAtLogin = StartWithWindowsCheck.IsChecked == true;
        config.Global.CloseToTray = CloseToTrayCheck.IsChecked == true;
        config.Global.PreventSleepWhileLit = PreventSleepCheck.IsChecked == true;
        config.Ai.Enabled = AiEnabledCheck.IsChecked == true;
        config.Ai.CursorShellGate = Selected<CursorShellGate>(ShellGateCombo) ?? config.Ai.CursorShellGate;
        config.Ai.CursorMcpGate = Selected<CursorMcpGate>(McpGateCombo) ?? config.Ai.CursorMcpGate;
        config.Ai.GateFileTools = FileToolGateCheck.IsChecked == true;
        config.Ai.CodexTouchApproval = CodexTouchCheck.IsChecked == true;
        config.Ai.ApprovalTimeoutSeconds = ParseInt(
            ApprovalTimeoutText.Text,
            config.Ai.ApprovalTimeoutSeconds);

        config.SystemMonitor.Metric = Selected<SystemMetric>(SystemMetricCombo) ?? config.SystemMonitor.Metric;
        config.SystemMonitor.Visualizer = Selected<SystemVisualizer>(SystemVisualizerCombo) ?? config.SystemMonitor.Visualizer;
        config.SystemMonitor.DisplayMinimum = ParseDouble(SystemMinText.Text, config.SystemMonitor.DisplayMinimum);
        config.SystemMonitor.DisplayMaximum = ParseDouble(SystemMaxText.Text, config.SystemMonitor.DisplayMaximum);
        config.SystemMonitor.UpdateIntervalMilliseconds = (int)SystemIntervalSlider.Value;
        var systemProfile = GetProfile(config.SystemMonitor.ProfileId);
        systemProfile.Primary = GetColor(SystemLowColorButton);
        systemProfile.Secondary = GetColor(SystemHighColorButton);

        config.Music.Visualizer = Selected<MusicVisualizer>(MusicVisualizerCombo) ?? config.Music.Visualizer;
        config.Music.Sensitivity = MusicSensitivitySlider.Value;
        config.Music.NoiseGate = MusicNoiseGateSlider.Value;
        config.Music.Attack = MusicSmoothingSlider.Value;
        config.Music.Release = MusicSmoothingSlider.Value;
        config.Music.FramesPerSecond = (int)MusicFpsSlider.Value;
        var musicProfile = GetProfile(config.Music.ProfileId);
        musicProfile.Primary = GetColor(MusicPrimaryColorButton);
        musicProfile.Secondary = GetColor(MusicSecondaryColorButton);

        SaveTouchBindings(TouchContext.Normal, NormalClickCombo, NormalDoubleCombo, NormalLongCombo);
        SaveTouchBindings(TouchContext.Permission, PermissionClickCombo, PermissionDoubleCombo, PermissionLongCombo);
        SaveTouchBindings(TouchContext.SystemMonitor, SystemClickCombo, SystemDoubleCombo, SystemLongCombo);
        SaveTouchBindings(TouchContext.Music, MusicClickCombo, MusicDoubleCombo, MusicLongCombo);
        config.Touch.Enabled = TouchEnabledCheck.IsChecked == true;
        AppConfigDefaults.Normalize(config);
    }

    private void SaveCurrentAiProfile()
    {
        if (!config.Ai.StateProfiles.TryGetValue(editingAiState, out var id))
        {
            return;
        }
        var target = GetProfile(id);
        var updated = ReadCurrentAiProfile(clone: false);
        target.Effect = updated.Effect;
        target.Primary = updated.Primary;
        target.Secondary = updated.Secondary;
        target.Brightness = updated.Brightness;
        target.Speed = updated.Speed;
        target.Start = updated.Start;
        target.Count = updated.Count;
        target.OnMilliseconds = updated.OnMilliseconds;
        target.OffMilliseconds = updated.OffMilliseconds;
        target.Pulses = updated.Pulses;
        target.GapMilliseconds = updated.GapMilliseconds;
        target.Reverse = updated.Reverse;
        target.Gamma = updated.Gamma;
        target.Fade = updated.Fade;
        target.MaxMilliamps = updated.MaxMilliamps;
        target.Palette = updated.Palette;
    }

    private LightProfile ReadCurrentAiProfile(bool clone)
    {
        var id = config.Ai.StateProfiles[editingAiState];
        var source = GetProfile(id);
        return new LightProfile
        {
            Id = clone ? $"preview-{id}" : id,
            Name = source.Name,
            Effect = Selected<RgbEffect>(EffectModeCombo) ?? source.Effect,
            Primary = GetColor(PrimaryColorButton),
            Secondary = GetColor(SecondaryColorButton),
            Brightness = (int)ProfileBrightnessSlider.Value,
            Speed = (int)EffectSpeedSlider.Value,
            Start = ParseInt(StartLedText.Text, source.Start),
            Count = ParseInt(LedCountText.Text, source.Count),
            OnMilliseconds = ParseInt(BlinkOnText.Text, source.OnMilliseconds),
            OffMilliseconds = ParseInt(BlinkOffText.Text, source.OffMilliseconds),
            Pulses = ParseInt(BlinkPulsesText.Text, source.Pulses),
            GapMilliseconds = ParseInt(BlinkGapText.Text, source.GapMilliseconds),
            Reverse = ReverseCheck.IsChecked == true,
            Gamma = GammaCheck.IsChecked == true,
            Fade = FadeCheck.IsChecked == true,
            MaxMilliamps = ParseInt(MaxMaText.Text, source.MaxMilliamps),
            Palette = (int)(Selected<PaletteChoice>(PaletteCombo) ?? (PaletteChoice)source.Palette),
        };
    }

    private void LoadTouchBindings(TouchContext context, ComboBox click, ComboBox doubleClick, ComboBox longPress)
    {
        var bindings = config.Touch.Contexts[context];
        bindings.Click = SelectTouch(click, bindings.Click);
        bindings.DoubleClick = SelectTouch(doubleClick, bindings.DoubleClick);
        bindings.LongPress = SelectTouch(longPress, bindings.LongPress);
    }

    private void SaveTouchBindings(TouchContext context, ComboBox click, ComboBox doubleClick, ComboBox longPress)
    {
        var bindings = config.Touch.Contexts[context];
        bindings.Click = Selected<TouchAction>(click) ?? bindings.Click;
        bindings.DoubleClick = Selected<TouchAction>(doubleClick) ?? bindings.DoubleClick;
        bindings.LongPress = Selected<TouchAction>(longPress) ?? bindings.LongPress;
    }

    private IEnumerable<ComboBox> TouchCombos()
    {
        yield return NormalClickCombo;
        yield return NormalDoubleCombo;
        yield return NormalLongCombo;
        yield return PermissionClickCombo;
        yield return PermissionDoubleCombo;
        yield return PermissionLongCombo;
        yield return SystemClickCombo;
        yield return SystemDoubleCombo;
        yield return SystemLongCombo;
        yield return MusicClickCombo;
        yield return MusicDoubleCombo;
        yield return MusicLongCombo;
    }

    private LightProfile GetProfile(string id) => config.Profiles.TryGetValue(id, out var profile)
        ? profile
        : config.Profiles.Values.First();

    private void OnDeviceStatusChanged(object? sender, DeviceStatus status) => Dispatcher.BeginInvoke(() =>
    {
        ConnectionDot.Fill = status.Connected ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        ConnectionText.Text = status.Message;
    });

    private void OnTouchDispatched(object? sender, TouchDispatch dispatch) => Dispatcher.BeginInvoke(() =>
    {
        LastTouchText.Text = $"触摸：{GestureLabel(dispatch.Gesture)} → {TouchActionLabel(dispatch.Action)}";
    });

    private void OnMetricChanged(object? sender, MetricReading reading) => Dispatcher.BeginInvoke(() =>
    {
        if (reading.Metric != config.SystemMonitor.Metric) return;
        var sample = reading.Sample;
        if (sample.Available && sample.Value is { } value)
        {
            var temperature = reading.Metric is SystemMetric.CpuTemperature or SystemMetric.GpuTemperature or SystemMetric.DiskTemperature;
            MetricValueText.Text = temperature ? $"{value:0.#} °C" : $"{value:0.#}%";
            MetricStatusText.Text = sample.Source;
        }
        else
        {
            MetricValueText.Text = "不可用";
            MetricStatusText.Text = sample.Reason;
        }
    });

    private void OnAudioLevelChanged(object? sender, AudioLevelSample sample) => Dispatcher.BeginInvoke(() =>
    {
        AudioLevelBar.Value = sample.Available ? sample.Peak : 0;
        AudioStatusText.Text = sample.Available ? sample.Source : sample.Reason;
    });

    private void OnShowRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowFromTray);

    private void OnShutdownRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => _ = ExitAsync());

    private void OnOrchestratorConfigChanged(object? sender, AppConfig updated) => Dispatcher.BeginInvoke(() =>
    {
        if (exiting)
        {
            return;
        }

        var remote = CloneConfig(updated);
        if (!uiReady)
        {
            config = remote;
            syncedConfig = CloneConfig(remote);
            return;
        }

        if (uiDirty)
        {
            // Capture controls before loading the device snapshot. Merge it
            // against the last configuration both sides observed, preserving
            // unrelated touch changes without discarding the local draft.
            ReadUiIntoConfig();
            config = AppConfigMerge.PreserveLocalDraft(syncedConfig, config, remote);
            syncedConfig = CloneConfig(remote);
            LoadConfigIntoUi();
            ScheduleSave(immediate: true);
            return;
        }

        config = remote;
        syncedConfig = CloneConfig(remote);
        LoadConfigIntoUi();
    });

    private void SetupTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 Pico Vibe", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(() => _ = ExitAsync()));
        trayDrawingIcon = LoadApplicationIcon();
        trayIcon = new Forms.NotifyIcon
        {
            Text = "Pico Vibe",
            Icon = trayDrawingIcon,
            Visible = true,
            ContextMenuStrip = menu,
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            try
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(executablePath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting)
        {
            return;
        }
        e.Cancel = true;
        if (uiReady && trayIcon is not null && config.Global.CloseToTray)
        {
            Hide();
            trayIcon?.ShowBalloonTip(1200, "Pico Vibe", "灯光和触摸服务仍在后台运行。", Forms.ToolTipIcon.Info);
        }
        else
        {
            _ = ExitAsync();
        }
    }

    private async Task ExitAsync()
    {
        if (exiting)
        {
            return;
        }
        exiting = true;
        saveTimer.Stop();
        if (uiReady)
        {
            await SaveConfigurationAsync(allowDuringExit: true);
        }
        uiReady = false;
        if (orchestrator is not null)
        {
            orchestrator.DeviceStatusChanged -= OnDeviceStatusChanged;
            orchestrator.TouchDispatched -= OnTouchDispatched;
            orchestrator.MetricChanged -= OnMetricChanged;
            orchestrator.AudioLevelChanged -= OnAudioLevelChanged;
            orchestrator.ShowRequested -= OnShowRequested;
            orchestrator.ShutdownRequested -= OnShutdownRequested;
            orchestrator.ConfigChanged -= OnOrchestratorConfigChanged;
            await orchestrator.DisposeAsync();
        }
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }
        trayDrawingIcon?.Dispose();
        trayDrawingIcon = null;
        Application.Current.Shutdown();
    }

    private static void UpdateStartupRegistration(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                key.SetValue(StartupValueName, $"\"{executable}\" --autostart", RegistryValueKind.String);
            }
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    private static void SetOptions<T>(ComboBox combo, params (T Value, string Label)[] values) where T : struct, Enum =>
        combo.ItemsSource = values.Select(item => new Option<T>(item.Value, item.Label)).ToArray();

    private static void SetTouchOptions(
        IEnumerable<ComboBox> combos,
        IEnumerable<TouchAction> actions)
    {
        var options = actions
            .Distinct()
            .Select(action => (action, TouchActionLabel(action)))
            .ToArray();
        foreach (var combo in combos)
        {
            SetOptions(combo, options);
        }
    }

    private static TouchAction SelectTouch(ComboBox combo, TouchAction action)
    {
        Select(combo, action);
        if (combo.SelectedItem is null)
        {
            Select(combo, TouchAction.None);
            return TouchAction.None;
        }
        return action;
    }

    private static void Select<T>(ComboBox combo, T value) where T : struct, Enum =>
        combo.SelectedItem = combo.Items.Cast<Option<T>>().FirstOrDefault(item => EqualityComparer<T>.Default.Equals(item.Value, value));

    private static T? Selected<T>(ComboBox combo) where T : struct, Enum =>
        combo.SelectedItem is Option<T> option ? option.Value : null;

    private static void SetColor(Button button, RgbColor color)
    {
        var clamped = new RgbColor(Math.Clamp(color.R, 0, 255), Math.Clamp(color.G, 0, 255), Math.Clamp(color.B, 0, 255));
        button.Tag = clamped;
        button.Content = $"#{clamped.R:X2}{clamped.G:X2}{clamped.B:X2}";
        button.Background = new SolidColorBrush(Color.FromRgb((byte)clamped.R, (byte)clamped.G, (byte)clamped.B));
        button.Foreground = (clamped.R * 299 + clamped.G * 587 + clamped.B * 114) / 1000 > 145
            ? Brushes.Black
            : Brushes.White;
    }

    private static RgbColor GetColor(Button button) => button.Tag is RgbColor color ? color : RgbColor.Black;

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : fallback;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : fallback;

    private static AppConfig CloneConfig(AppConfig source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, ConfigStore.JsonOptions);
        return AppConfigDefaults.Normalize(
            JsonSerializer.Deserialize<AppConfig>(bytes, ConfigStore.JsonOptions));
    }

    private static string TouchActionLabel(TouchAction action) => action switch
    {
        TouchAction.None => "不执行",
        TouchAction.NextPreset => "下一个灯效",
        TouchAction.PreviousPreset => "上一个灯效",
        TouchAction.ToggleLights => "开关灯光",
        TouchAction.CyclePrimaryMode => "切换主模式",
        TouchAction.ShowApplication => "打开控制面板",
        TouchAction.AllowRequest => "批准请求",
        TouchAction.DenyRequest => "拒绝请求",
        TouchAction.CancelRequest => "取消请求",
        TouchAction.NextMetric => "下一个监控项",
        TouchAction.NextVisualizer => "下一个显示样式",
        TouchAction.TogglePause => "暂停 / 继续灯效",
        TouchAction.NextTrack => "下一曲",
        _ => action.ToString(),
    };

    private static string GestureLabel(TouchGesture gesture) => gesture switch
    {
        TouchGesture.Click => "单击",
        TouchGesture.DoubleClick => "双击",
        TouchGesture.LongPress => "长按",
        _ => "无",
    };

    private sealed record Option<T>(T Value, string Label) where T : struct, Enum
    {
        public override string ToString() => Label;
    }

    private enum PaletteChoice
    {
        Rainbow = 0,
        Warm = 1,
        Ice = 2,
        Neon = 3,
    }
}
