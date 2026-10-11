using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Lighting;

namespace PicoVibe.Mac;

public partial class MainWindow
{
    private DispatcherTimer? agentUiTimer;
    private readonly List<Button> agentLampButtons = new();
    private AgentLightState editingAgentState;
    private bool loadingAgentProfile;
    private bool refreshingAgentList;
    private string? selectedAgentKey;
    private DateTimeOffset lastAgentListUpdate;

    public sealed record AgentRow(string Key, string LampLabel, string Name, string StateLabel, string ActivityLabel, bool Assigned);

    private void PopulateAgentOptions()
    {
        AiDisplayModeCombo.ItemsSource = Enum.GetValues<AiDisplayMode>();
        AgentLightStateCombo.ItemsSource = Enum.GetValues<AgentLightState>();
        AgentLightEffectCombo.ItemsSource = Enum.GetValues<AgentLightEffect>();
        for (var i = 0; i < 12; i++)
        {
            var button = new Button { Content = (i + 1).ToString("00"), Margin = new Thickness(2),
                Padding = new Thickness(0, 10), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            button.Click += (_, _) =>
            {
                if (button.Tag is string key && AgentList.ItemsSource is IEnumerable<AgentRow> rows)
                    AgentList.SelectedItem = rows.FirstOrDefault(r => r.Key == key);
            };
            agentLampButtons.Add(button);
            AgentLampStrip.Children.Add(button);
        }
    }

    private void LoadAgentSettings()
    {
        AiDisplayModeCombo.SelectedItem = config.Ai.DisplayMode;
        AgentReverseCheck.IsChecked = config.Ai.MultiAgent.Reverse;
        AgentLightStateCombo.SelectedItem = editingAgentState;
        LoadAgentProfile();
    }

    private void WireAgentEvents()
    {
        AiDisplayModeCombo.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            CaptureAgentSettings();
            UpdateAgentModeVisibility();
            ScheduleSave();
        };
        AgentLightStateCombo.SelectionChanged += (_, _) =>
        {
            if (loading || loadingAgentProfile) return;
            CaptureAgentProfile();
            if (AgentLightStateCombo.SelectedItem is AgentLightState state) editingAgentState = state;
            LoadAgentProfile();
            ScheduleSave();
        };
        AgentLightEffectCombo.SelectionChanged += OnAgentProfileEdited;
        AgentColorPicker.ColorChanged += OnAgentProfileEdited;
        AgentBrightnessNumber.ValueChanged += OnAgentProfileEdited;
        AgentPeriodNumber.ValueChanged += OnAgentProfileEdited;
        AgentReverseCheck.IsCheckedChanged += OnAgentProfileEdited;
        AgentList.SelectionChanged += (_, _) =>
        {
            if (refreshingAgentList) return;
            var row = AgentList.SelectedItem as AgentRow;
            selectedAgentKey = row?.Key;
            AgentNameText.Text = row?.Name ?? "";
        };
        AgentRenameButton.Click += (_, _) =>
        {
            if (selectedAgentKey is null) return;
            var name = AgentNameText.Text?.Trim();
            if (string.IsNullOrEmpty(name)) config.Ai.MultiAgent.AgentNames.Remove(selectedAgentKey);
            else config.Ai.MultiAgent.AgentNames[selectedAgentKey] = name;
            ScheduleSave();
        };
        AgentLocateButton.Click += (_, _) =>
        {
            FooterText.Text = selectedAgentKey is not null && orchestrator?.LocateAgent(selectedAgentKey) == true
                ? "对应灯珠正在短暂亮白。" : "请选择已分配灯位的 Agent；授权期间暂停定位。";
        };
    }

    private void OnAgentProfileEdited(object? sender, EventArgs e)
    {
        if (loading || loadingAgentProfile) return;
        CaptureAgentSettings();
        ScheduleSave();
    }

    private void CaptureAgentSettings()
    {
        if (AiDisplayModeCombo.SelectedItem is AiDisplayMode mode) config.Ai.DisplayMode = mode;
        config.Ai.MultiAgent.Reverse = AgentReverseCheck.IsChecked == true;
        CaptureAgentProfile();
    }

    private void CaptureAgentProfile()
    {
        if (loadingAgentProfile || AgentLightEffectCombo.SelectedItem is not AgentLightEffect effect) return;
        var profile = config.Ai.MultiAgent.Profiles[editingAgentState];
        profile.Effect = effect;
        profile.Color = FromPickerColor(AgentColorPicker.Color);
        profile.BrightnessPercent = NumberInt(AgentBrightnessNumber, profile.BrightnessPercent);
        profile.PeriodMilliseconds = NumberInt(AgentPeriodNumber, profile.PeriodMilliseconds);
    }

    private void LoadAgentProfile()
    {
        loadingAgentProfile = true;
        try
        {
            var profile = config.Ai.MultiAgent.Profiles[editingAgentState];
            AgentLightEffectCombo.SelectedItem = profile.Effect;
            AgentColorPicker.Color = ToPickerColor(profile.Color);
            AgentBrightnessNumber.Value = profile.BrightnessPercent;
            AgentPeriodNumber.Value = profile.PeriodMilliseconds;
        }
        finally { loadingAgentProfile = false; }
    }

    private void UpdateAgentModeVisibility()
    {
        var multi = config.Ai.DisplayMode == AiDisplayMode.PerAgent;
        MultiAgentPanel.IsVisible = multi;
        OverallAgentEditor.IsVisible = !multi;
        var expanded = NavigationList.SelectedIndex == 0 && multi;
        ContentColumns.ColumnDefinitions[1].Width = new GridLength(expanded ? 0 : 340);
        LightEditorPanel.IsVisible = NavigationList.SelectedIndex is >= 0 and <= 2 && !expanded;
    }

    private void StartAgentUiTimer()
    {
        agentUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        agentUiTimer.Tick += (_, _) => RefreshAgentView();
        agentUiTimer.Start();
    }

    private void RefreshAgentView()
    {
        if (orchestrator is null || !IsVisible || !MultiAgentPanel.IsVisible || !AiPage.IsVisible) return;
        var view = orchestrator.GetAgentLampView();
        for (var i = 0; i < agentLampButtons.Count; i++)
        {
            var lamp = view.Lamps.FirstOrDefault(l => l.Slot is { } slot &&
                AgentFrameRenderer.PhysicalSlot(slot, view.Capacity, config.Ai.MultiAgent.Reverse) == i);
            var button = agentLampButtons[i];
            button.Content = i < view.Capacity
                ? (AgentFrameRenderer.PhysicalSlot(i, view.Capacity, config.Ai.MultiAgent.Reverse) + 1).ToString("00") : "—";
            button.Background = view.Frame[i * 3] + view.Frame[i * 3 + 1] + view.Frame[i * 3 + 2] == 0
                ? LedOffBrush() : new SolidColorBrush(Color.FromRgb(view.Frame[i * 3], view.Frame[i * 3 + 1], view.Frame[i * 3 + 2]));
            button.Foreground = Brushes.White;
            button.Tag = lamp?.Agent.Key;
            button.IsEnabled = lamp is not null;
            ToolTip.SetTip(button, lamp?.Agent.Name ?? (i < view.Capacity ? "未分配" : "不可用灯位"));
        }
        var unassigned = view.Lamps.Count(l => l.Slot is null);
        AgentCapacityText.Text = $"已分配 {view.Lamps.Count(l => l.Slot is not null)} / {view.Capacity} 颗灯" +
            (unassigned > 0 ? $" · {unassigned} 个 Agent 未分配，需在客户端处理授权。" : " · 点击灯珠可选中对应 Agent。");
        var current = view.Lamps.FirstOrDefault(l => l.TouchReady);
        AgentTouchText.Text = current is null ? "当前没有可触摸批准的请求" : $"当前触摸操作：灯 {current.Slot + 1:00} / {current.Agent.Name}";
        AgentEmptyText.IsVisible = view.Lamps.Count == 0;
        AgentRenameButton.IsEnabled = selectedAgentKey is not null;
        AgentLocateButton.IsEnabled = selectedAgentKey is not null && !view.Lamps.Any(l => l.TouchReady || l.Agent.State == AiState.Permission) &&
            view.Lamps.Any(l => l.Agent.Key == selectedAgentKey && l.Slot is not null);
        var now = DateTimeOffset.UtcNow;
        if (now - lastAgentListUpdate < TimeSpan.FromSeconds(1)) return;
        lastAgentListUpdate = now;
        var converter = new EnumLabelConverter();
        var rows = view.Lamps.Select(l => new AgentRow(l.Agent.Key, l.Slot is { } slot ? $"灯 {slot + 1:00}" : "未分配", l.Agent.Name,
            converter.Convert(AgentFrameRenderer.StateFor(l), typeof(string), null, System.Globalization.CultureInfo.CurrentCulture).ToString()!,
            now - l.Agent.LastActivity < TimeSpan.FromSeconds(5) ? "刚刚" : $"{Math.Max(0, (int)(now - l.Agent.LastActivity).TotalSeconds)} 秒前", l.Slot is not null)).ToArray();
        refreshingAgentList = true;
        try
        {
            AgentList.ItemsSource = rows;
            AgentList.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedAgentKey);
            if (selectedAgentKey is not null && AgentList.SelectedItem is null)
            { selectedAgentKey = null; AgentNameText.Text = ""; }
        }
        finally { refreshingAgentList = false; }
    }
}
