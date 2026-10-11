using System.Collections.Concurrent;
using System.Buffers.Binary;
using PicoVibe.App.Services;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Device;
using PicoVibe.Core.Lighting;
using PicoVibe.Core.Ipc;

namespace PicoVibe.App.Tests;

public sealed class LightOrchestratorTests
{
    [Fact]
    public async Task MultiAgentFramesKeepToolsAndThinkingOnSeparateLampsAndReleaseOnModeSwitch()
    {
        using var directory = new TestDirectory();
        var config = AppConfig.CreateDefault();
        config.Ai.DisplayMode = AiDisplayMode.PerAgent;
        config.Global.BrightnessPercent = 100;
        config.Ai.MultiAgent.Profiles[AgentLightState.Thinking] = new() { Color = new(255, 0, 0), BrightnessPercent = 100 };
        config.Ai.MultiAgent.Profiles[AgentLightState.Tools] = new() { Color = new(0, 0, 255), BrightnessPercent = 100 };
        var transport = new FakeTransport();
        await using var orchestrator = Create(directory, config, new PicoDeviceLocation(directory.Path), transport);
        await orchestrator.HandleHookAsync(Request(HookEvent.PromptSubmitted) with { SessionId = "a" }, CancellationToken.None);
        await orchestrator.HandleHookAsync(Request(HookEvent.ToolStarted) with { SessionId = "b" }, CancellationToken.None);
        orchestrator.Start();
        await WaitUntilAsync(() => transport.VibePackets.Any(p => p.AsSpan(32, 36).ToArray().Count(v => v == 255) == 2));
        var view = orchestrator.GetAgentLampView();
        Assert.Equal(2, view.Lamps.Count);
        Assert.Equal(2, view.Lamps.Select(l => l.Slot).Distinct().Count());
        Assert.Null(transport.LastRgbCommand);
        config.Ai.DisplayMode = AiDisplayMode.Overall;
        orchestrator.UpdateConfig(config);
        await WaitUntilAsync(() => !transport.ReleasePackets.IsEmpty && transport.LastRgbCommand is not null);
    }

    [Fact]
    public async Task MultiAgentWithoutSlotNeverArmsTouchApproval()
    {
        using var directory = new TestDirectory();
        var config = AppConfig.CreateDefault();
        config.Ai.DisplayMode = AiDisplayMode.PerAgent;
        config.Global.LedCount = 1;
        var transport = new FakeTransport();
        await using var orchestrator = Create(directory, config, new PicoDeviceLocation(directory.Path), transport);
        await orchestrator.HandleHookAsync(Request(HookEvent.ToolStarted) with { SessionId = "occupied" }, CancellationToken.None);
        Assert.NotNull(Assert.Single(orchestrator.GetAgentLampView().Lamps).Slot);
        var result = await orchestrator.HandleHookAsync(Request(HookEvent.PermissionRequested, HookPlatform.Codex)
            with { SessionId = "overflow" }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HookDecision.None, result.Decision);
        Assert.Contains("灯位", result.Message, StringComparison.Ordinal);
        Assert.Null(orchestrator.GetAgentLampView().TouchAgentKey);
    }

    [Fact]
    public async Task MultiAgentOnlyCurrentPermissionIsTouchReadyAndLocateIsBlocked()
    {
        using var directory = new TestDirectory();
        var config = AppConfig.CreateDefault();
        config.Ai.DisplayMode = AiDisplayMode.PerAgent;
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 1);
        await using var orchestrator = Create(directory, config, new PicoDeviceLocation(directory.Path), transport);
        orchestrator.Start();
        using var cancel = new CancellationTokenSource();
        var current = orchestrator.HandleHookAsync(Request(HookEvent.PermissionRequested, HookPlatform.Codex)
            with { SessionId = "current" }, cancel.Token);
        await WaitUntilAsync(() => orchestrator.GetAgentLampView().TouchAgentKey is not null);
        await orchestrator.HandleHookAsync(Request(HookEvent.PermissionRequested, HookPlatform.Claude)
            with { SessionId = "native", ObserveOnly = true }, CancellationToken.None);
        var view = orchestrator.GetAgentLampView();
        Assert.Single(view.Lamps, l => l.TouchReady);
        Assert.Single(view.Lamps, l => AgentFrameRenderer.StateFor(l) == AgentLightState.NativePermission);
        Assert.False(orchestrator.LocateAgent(view.Lamps[0].Agent.Key));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => current);
    }

    [Theory]
    [InlineData(HookPlatform.Codex)]
    [InlineData(HookPlatform.Claude)]
    public async Task ObservedNativeApprovalKeepsYellowButDoesNotConsumeTouch(HookPlatform platform)
    {
        using var directory = new TestDirectory();
        var config = AppConfig.CreateDefault();
        config.Ai.CodexTouchApproval = false;
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 1);
        await using var orchestrator = Create(directory, config, new PicoDeviceLocation(directory.Path), transport);
        orchestrator.Start();
        var request = Request(HookEvent.PermissionRequested, platform) with { SessionId = "native", ObserveOnly = true };
        var response = await orchestrator.HandleHookAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HookDecision.None, response.Decision);
        await WaitUntilAsync(() => transport.LastRgbCommand?.Contains("MODE=solid,R=255,G=200,B=0", StringComparison.Ordinal) == true);
        TouchDispatch? touch = null;
        orchestrator.TouchDispatched += (_, value) => touch = value;
        await WaitUntilAsync(() => transport.TouchReadCount >= 2);
        transport.SetTouch(1, "click", 2);
        await WaitUntilAsync(() => touch is not null);
        Assert.NotEqual(TouchContext.Permission, touch!.Context);
        await orchestrator.HandleHookAsync(request with { RequestId = "stop", Timestamp = DateTimeOffset.UtcNow,
            Event = HookEvent.AgentStopped }, CancellationToken.None);
        await WaitUntilAsync(() => transport.LastRgbCommand?.Contains("G=255,B=72", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RestartDoesNotRestoreHistoricalSessionsOrPermissions()
    {
        using var directory = new TestDirectory();
        var journal = new HookEventJournal(directory.File("hook-events"));
        journal.Append(Request(HookEvent.PermissionRequested, HookPlatform.Codex) with { SessionId = "restore" });
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 1);
        await using var orchestrator = Create(directory, AppConfig.CreateDefault(), new PicoDeviceLocation(directory.Path), transport);
        orchestrator.Start();
        await WaitUntilAsync(() => transport.LastRgbCommand is not null);
        Assert.Empty(orchestrator.GetAgentLampView().Lamps);
        Assert.DoesNotContain("MODE=solid,R=255,G=200,B=0", transport.LastRgbCommand!);
        TouchDispatch? touch = null;
        orchestrator.TouchDispatched += (_, value) => touch = value;
        await WaitUntilAsync(() => transport.TouchReadCount >= 2);
        transport.SetTouch(1, "click", 2);
        await WaitUntilAsync(() => touch is not null);
        Assert.NotEqual(TouchContext.Permission, touch!.Context);
        journal.Append(Request(HookEvent.ToolStarted, HookPlatform.Codex) with { SessionId = "fresh" });
        await WaitUntilAsync(() => orchestrator.GetAgentLampView().Lamps.Count == 1);
    }

    [Fact]
    public async Task EarlyTouchCannotApproveLaterPermissionRequest()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.Ai;
        config.Ai.ApprovalTimeoutSeconds = 5;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.TouchReadCount >= 2);
        await orchestrator.HandleHookAsync(
            Request(HookEvent.ToolStarted),
            CancellationToken.None);
        await WaitUntilAsync(() => transport.LastRgbCommand?.StartsWith("MODE=chase", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("R=255,G=200,B=0", transport.LastRgbCommand, StringComparison.Ordinal);

        // This click occurs while Tools/running is active. It is consumed in the
        // Normal context and must never be carried into a future ask.
        transport.SetTouch(1, "click", 100);
        await WaitUntilAsync(() => transport.LastRgbCommand?.StartsWith("MODE=solid", StringComparison.Ordinal) == true);

        var permission = orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);
        await WaitUntilAsync(() =>
            transport.LastRgbCommand?.Contains("MODE=blink,R=255,G=200,B=0", StringComparison.Ordinal) == true);
        await Task.Delay(250);
        Assert.False(permission.IsCompleted);

        transport.SetTouch(2, "click", 200);
        var response = await permission.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HookDecision.Allow, response.Decision);
    }

    [Theory]
    [InlineData(HookPlatform.Cursor, TouchGesture.Click, HookDecision.Allow, TouchAction.AllowRequest)]
    [InlineData(HookPlatform.Cursor, TouchGesture.DoubleClick, HookDecision.Deny, TouchAction.DenyRequest)]
    [InlineData(HookPlatform.Codex, TouchGesture.Click, HookDecision.Allow, TouchAction.AllowRequest)]
    [InlineData(HookPlatform.Codex, TouchGesture.DoubleClick, HookDecision.Deny, TouchAction.DenyRequest)]
    [InlineData(HookPlatform.Claude, TouchGesture.Click, HookDecision.Allow, TouchAction.AllowRequest)]
    [InlineData(HookPlatform.Claude, TouchGesture.DoubleClick, HookDecision.Deny, TouchAction.DenyRequest)]
    public async Task PermissionGestureUsesYellowLightAndReturnsPlatformDecision(
        HookPlatform platform,
        TouchGesture gesture,
        HookDecision expectedDecision,
        TouchAction expectedAction)
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.Ai;
        config.Ai.ApprovalTimeoutSeconds = 5;
        await using var orchestrator = Create(directory, config, device, transport);
        TouchDispatch? dispatched = null;
        orchestrator.TouchDispatched += (_, touch) => dispatched = touch;
        orchestrator.Start();

        var permission = orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested, platform),
            CancellationToken.None);
        await WaitUntilAsync(() =>
            transport.LastRgbCommand?.Contains(
                "MODE=blink,R=255,G=200,B=0",
                StringComparison.Ordinal) == true);

        transport.SetTouch(
            1,
            gesture == TouchGesture.Click ? "click" : "dblclick",
            100);
        var response = await permission.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => dispatched is not null);

        Assert.Equal(expectedDecision, response.Decision);
        Assert.Equal(gesture, dispatched!.Gesture);
        Assert.Equal(expectedAction, dispatched.Action);
        Assert.Equal(TouchContext.Permission, dispatched.Context);
    }

    [Fact]
    public async Task PermissionTimeoutReturnsNoDecisionForNativePromptFallback()
    {
        using var directory = new TestDirectory();
        var config = AppConfig.CreateDefault();
        config.Ai.ApprovalTimeoutSeconds = 5;
        await using var orchestrator = Create(
            directory,
            config,
            new PicoDeviceLocation(directory.Path),
            new FakeTransport());

        var started = DateTimeOffset.UtcNow;
        var response = await orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);

        Assert.Equal(HookDecision.None, response.Decision);
        Assert.Contains("正常询问", response.Message, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(4.5));
    }

    [Fact]
    public async Task DynamicModeUsesVibeAndReleasesBeforeFirmwareProfile()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.SystemMonitor;
        config.Global.BrightnessPercent = 100;
        config.Profiles[config.SystemMonitor.ProfileId].Brightness = 37;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.VibePackets.Count > 0);
        var frame = transport.VibePackets.Last();
        Assert.Equal("VIBE", System.Text.Encoding.ASCII.GetString(frame, 0, 4));
        Assert.Equal((byte)VibePacketKind.Frame, frame[5]);
        Assert.Equal(37, frame[20]);
        Assert.Equal(VibePacketEncoder.LedCount, frame[21]);

        config.Global.ActiveMode = PrimaryMode.Ai;
        orchestrator.UpdateConfig(config);
        await WaitUntilAsync(() => transport.ReleasePackets.Count > 0 && transport.LastRgbCommand is not null);
        Assert.Equal((byte)VibePacketKind.Release, transport.ReleasePackets.Last()[5]);
        Assert.StartsWith("MODE=breathe", transport.LastRgbCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicWriteFailureResetsTransportAndRecoversOnSameRoot()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        transport.FailVibeWritesUntilReset();
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.SystemMonitor;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.ResetConnectionCount >= 1);
        await WaitUntilAsync(() => transport.VibePackets.Count >= 1);

        Assert.Equal(1, transport.ResetConnectionCount);
        Assert.Equal((byte)VibePacketKind.Frame, transport.VibePackets.Last()[5]);
    }

    [Fact]
    public async Task TouchReadFailureResetsTransportAndRecoversOnSameRoot()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        transport.FailTouchReadsUntilReset();
        var config = AppConfig.CreateDefault();
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.ResetConnectionCount >= 1);
        await WaitUntilAsync(() => transport.TouchReadCount >= 2);

        Assert.Equal(1, transport.ResetConnectionCount);
    }

    /// <summary>
    /// The card runs at about 400 kHz by design, so a frame costs tens of
    /// milliseconds. Asking for 30 a second on that link leaves nothing for the
    /// touch poll or for the OS's own access, and the backlog is what makes the
    /// volume look dead. The requested rate has to be a ceiling, not a promise.
    /// </summary>
    /// <summary>
    /// Music mode used to fill the three-stop gradient with primary, secondary,
    /// primary, which is symmetric about the middle: both ends came out the
    /// primary colour with the secondary in the centre. One colour per end is
    /// what the two settings mean.
    /// </summary>
    /// <summary>
    /// The device describes itself in INFO.TXT so one application can drive
    /// several hardware revisions; the orchestrator has to read it on connect.
    /// </summary>
    [Fact]
    public async Task ReadsWhatTheDeviceSaysAboutItself()
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "INFO.TXT"),
            "pico_vibe=1\nfw=2.23\nboard=production_v2\nboard_id=2\nproto=vibe1\nleds=12\n");
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        await using var orchestrator = Create(directory, AppConfig.CreateDefault(), device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => !orchestrator.DeviceInfo.IsLegacy);

        Assert.Equal("2.23", orchestrator.DeviceInfo.FirmwareVersion);
        Assert.Equal("production_v2", orchestrator.DeviceInfo.BoardName);
        Assert.Equal(2, orchestrator.DeviceInfo.BoardId);
    }

    /// <summary>
    /// A device running firmware from before INFO.TXT existed must still be
    /// driven, not rejected.
    /// </summary>
    [Fact]
    public async Task DeviceWithoutAnInfoFileStillConnects()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        await using var orchestrator = Create(directory, AppConfig.CreateDefault(), device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.VibePackets.Count >= 1 || transport.LastRgbCommand is not null);

        Assert.True(orchestrator.DeviceInfo.IsLegacy);
        Assert.Equal(12, orchestrator.DeviceInfo.LedCount);
    }

    [Fact]
    public async Task MusicGradientRunsFromPrimaryToSecondary()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var audio = new DrivenAudioLevelService();
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.Music;
        config.Global.BrightnessPercent = 100;
        config.Music.Visualizer = MusicVisualizer.VuMeter;
        config.Music.NoiseGate = 0;
        config.Music.Sensitivity = 5.0;
        var profile = config.Profiles[config.Music.ProfileId];
        profile.Primary = new RgbColor(255, 0, 0);
        profile.Secondary = new RgbColor(0, 0, 255);
        profile.Brightness = 255;
        profile.Reverse = false;
        profile.Start = 0;
        profile.Count = VibePacketEncoder.LedCount;
        await using var orchestrator = Create(directory, config, device, transport, audioService: audio);
        orchestrator.Start();

        // Full scale so every lamp in the meter is lit and carries its colour.
        for (var i = 0; i < 40; i++)
        {
            audio.Push(1.0, 1.0);
            await Task.Delay(25);
        }

        await WaitUntilAsync(() => transport.VibePackets.Count >= 2, TimeSpan.FromSeconds(10));
        var packet = transport.VibePackets.Last();
        int R(int led) => packet[VibePacketEncoder.RgbOffset + (led * 3)];
        int B(int led) => packet[VibePacketEncoder.RgbOffset + (led * 3) + 2];
        var last = VibePacketEncoder.LedCount - 1;

        Assert.True(R(0) > B(0), $"The first lamp should lean primary: R={R(0)} B={B(0)}.");
        Assert.True(B(last) > R(last), $"The last lamp should lean secondary: R={R(last)} B={B(last)}.");
    }

    [Fact]
    public async Task DynamicFramesArePacedToWhatTheLinkAnswers()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        transport.SlowVibeWrites(60);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.Music;
        config.Music.FramesPerSecond = 30;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.VibePackets.Count >= 1, TimeSpan.FromSeconds(10));
        var start = DateTimeOffset.UtcNow;
        var before = transport.VibePackets.Count;
        await Task.Delay(3000);
        var rate = (transport.VibePackets.Count - before) /
            (DateTimeOffset.UtcNow - start).TotalSeconds;

        // 60 ms writes pace to one every 180 ms, about 5.5 a second. Without
        // pacing the loop would issue them back to back, about 16 a second.
        Assert.True(rate < 9, $"Frames were not paced: {rate:F1} a second.");
        Assert.True(rate > 2, $"Frames stopped altogether: {rate:F1} a second.");
    }

    [Fact]
    public async Task UnresponsiveDeviceIsReportedAndRediscoveredInsteadOfFreezing()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        transport.WedgeTouchReadsUntilReset();
        var statuses = new ConcurrentQueue<string>();
        var config = AppConfig.CreateDefault();
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.DeviceStatusChanged += (_, status) => statuses.Enqueue(status.Message);
        orchestrator.Start();

        await WaitUntilAsync(
            () => transport.ResetConnectionCount >= 1,
            TimeSpan.FromSeconds(15));

        Assert.Contains(statuses, message => message.Contains("设备无响应", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentManualReconnectAndStaleTouchFailureResetOnceAndRecover()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();
        await WaitUntilAsync(() => transport.TouchReadCount >= 2);

        var blockedRead = transport.BlockAndFailNextTouchRead();
        await blockedRead.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        var failedReadCount = transport.TouchReadCount;
        var reconnect = orchestrator.ForceReconnectAsync();
        blockedRead.Release();

        await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => transport.TouchReadCount > failedReadCount);

        Assert.Equal(1, transport.ResetConnectionCount);
    }

    [Fact]
    public async Task MissingVolumeResetsTransportBeforeSameRootIsRediscovered()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.SystemMonitor;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();
        await WaitUntilAsync(() => transport.VibePackets.Count >= 1);

        Directory.Delete(directory.Path, recursive: true);
        await WaitUntilAsync(() => transport.ResetConnectionCount >= 1);
        var packetsBeforeRemount = transport.VibePackets.Count;
        directory.CreateDeviceFiles();

        await WaitUntilAsync(() => transport.VibePackets.Count > packetsBeforeRemount);
        Assert.Equal(1, transport.ResetConnectionCount);
    }

    [Fact]
    public async Task MusicDoubleClickSendsOneGlobalNextTrackCommand()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        var mediaController = new FakeSystemMediaController();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.Music;
        config.Touch.Contexts[TouchContext.Music].DoubleClick = TouchAction.NextTrack;
        await using var orchestrator = Create(
            directory,
            config,
            device,
            transport,
            mediaController: mediaController);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.TouchReadCount >= 2);
        transport.SetTouch(1, "dblclick", 500);
        await WaitUntilAsync(() => mediaController.NextTrackCount == 1);
        await Task.Delay(150);

        Assert.Equal(1, mediaController.NextTrackCount);
    }

    [Fact]
    public async Task AudioLifecycleFollowsMusicModeTransitionsWithoutPollingRetries()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        var audioService = new FakeAudioLevelService();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.Music;
        await using var orchestrator = Create(
            directory,
            config,
            device,
            transport,
            audioService: audioService);
        orchestrator.Start();

        // StartAsync deliberately leaves IsRunning false, matching a denied or
        // unavailable capture backend. Multiple music polls must not retry it.
        await WaitUntilAsync(() => audioService.AttackTimeSetCount >= 3);
        Assert.Equal(1, audioService.StartCount);
        Assert.Equal(0, audioService.StopCount);

        config.Global.ActiveMode = PrimaryMode.Ai;
        orchestrator.UpdateConfig(config);
        await WaitUntilAsync(() => audioService.StopCount == 1);
        await Task.Delay(650);
        Assert.Equal(1, audioService.StopCount);

        var attackAssignmentsBeforeReentry = audioService.AttackTimeSetCount;
        config.Global.ActiveMode = PrimaryMode.Music;
        orchestrator.UpdateConfig(config);
        await WaitUntilAsync(() =>
            audioService.AttackTimeSetCount >= attackAssignmentsBeforeReentry + 2);

        Assert.Equal(2, audioService.StartCount);
        Assert.Equal(1, audioService.StopCount);
    }

    [Fact]
    public async Task PermissionDoubleClickDeniesWithoutSendingNextTrack()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        var mediaController = new FakeSystemMediaController();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.Music;
        config.Ai.ApprovalTimeoutSeconds = 5;
        await using var orchestrator = Create(
            directory,
            config,
            device,
            transport,
            mediaController: mediaController);
        orchestrator.Start();

        var permission = orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);
        await WaitUntilAsync(() =>
            transport.LastRgbCommand?.Contains("MODE=blink,R=255,G=200,B=0", StringComparison.Ordinal) == true);
        transport.SetTouch(1, "dblclick", 500);

        var response = await permission.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HookDecision.Deny, response.Decision);
        Assert.Equal(0, mediaController.NextTrackCount);
    }

    [Fact]
    public async Task TouchObservedBeforePermissionIsDispatchedInItsOriginalContext()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Ai.ApprovalTimeoutSeconds = 5;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();
        await WaitUntilAsync(() => transport.TouchReadCount >= 2);

        var blockedRead = transport.SetTouchAndBlockNextRead(1, "click", 100);
        await blockedRead.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        var permission = orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);

        // The permission request is now waiting behind the touch transaction.
        // Releasing it must snapshot Normal before the permission can arm.
        blockedRead.Release();
        await WaitUntilAsync(() => transport.TouchReadCount >= 4);
        await Task.Delay(150);
        Assert.False(permission.IsCompleted);

        transport.SetTouch(2, "click", 200);
        var response = await permission.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HookDecision.Allow, response.Decision);
    }

    [Fact]
    public async Task PermissionImmediatelyAfterStartEstablishesBaselineBeforeAcceptingTouch()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Ai.ApprovalTimeoutSeconds = 5;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        var permission = orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);
        await WaitUntilAsync(() =>
            transport.LastRgbCommand?.Contains("MODE=blink,R=255,G=200,B=0", StringComparison.Ordinal) == true);

        transport.SetTouch(1, "click", 100);
        var response = await permission.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HookDecision.Allow, response.Decision);
    }

    [Fact]
    public async Task DeviceDisconnectEndsPermissionPromptWithoutWaitingForTimeout()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Ai.ApprovalTimeoutSeconds = 30;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        var permission = orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);
        await WaitUntilAsync(() =>
            transport.LastRgbCommand?.Contains("MODE=blink,R=255,G=200,B=0", StringComparison.Ordinal) == true);

        var started = DateTimeOffset.UtcNow;
        await orchestrator.ForceReconnectAsync();
        var response = await permission.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(HookDecision.None, response.Decision);
        Assert.Contains("正常询问", response.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.ResetConnectionCount);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PermissionOverridesAnActivePreviewImmediately()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Ai.ApprovalTimeoutSeconds = 5;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();
        orchestrator.Preview(new LightProfile
        {
            Id = "preview",
            Name = "preview",
            Effect = RgbEffect.Fire,
            Primary = new(255, 0, 0),
        }, TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => transport.LastRgbCommand?.StartsWith("MODE=fire", StringComparison.Ordinal) == true);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var permission = orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            cancellation.Token);
        await WaitUntilAsync(() =>
            transport.LastRgbCommand?.Contains("MODE=blink,R=255,G=200,B=0", StringComparison.Ordinal) == true);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => permission);
    }

    [Fact]
    public async Task OrchestratorOwnsADeepConfigurationSnapshot()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        await using var orchestrator = Create(directory, config, device, transport);

        config.Global.LightsEnabled = false;
        config.Profiles["ai-idle"].Effect = RgbEffect.Fire;
        orchestrator.Start();

        await WaitUntilAsync(() => transport.LastRgbCommand is not null);
        Assert.StartsWith("MODE=breathe", transport.LastRgbCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshZeroedMailboxesAcceptTheFirstDynamicFrame()
    {
        using var directory = new TestDirectory();
        File.WriteAllBytes(directory.File("VIBE0.BIN"), new byte[VibePacketEncoder.SectorSize]);
        File.WriteAllBytes(directory.File("VIBE1.BIN"), new byte[VibePacketEncoder.SectorSize]);
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.SystemMonitor;
        await using var orchestrator = Create(directory, config, device, transport);
        orchestrator.Start();

        await WaitUntilAsync(() => transport.VibePackets.Count > 0);
        Assert.Equal((byte)VibePacketKind.Frame, transport.VibePackets.Last()[5]);
    }

    [Fact]
    public async Task PersistedEpochAdvancesAcrossOrchestratorRestarts()
    {
        using var directory = new TestDirectory();
        var store = new VibeSessionEpochStore(directory.File("epoch.txt"));
        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.SystemMonitor;
        var device = new PicoDeviceLocation(directory.Path);
        uint firstEpoch;

        var firstTransport = new FakeTransport();
        firstTransport.SetTouch(0, "none", 10);
        await using (var first = Create(directory, config, device, firstTransport, store))
        {
            first.Start();
            await WaitUntilAsync(() => firstTransport.VibePackets.Count > 0);
            firstEpoch = EpochOf(firstTransport.VibePackets.Last());
        }

        var secondTransport = new FakeTransport();
        secondTransport.SetTouch(0, "none", 10);
        await using (var second = Create(directory, config, device, secondTransport, store))
        {
            second.Start();
            await WaitUntilAsync(() => secondTransport.VibePackets.Count > 0);
            var secondEpoch = EpochOf(secondTransport.VibePackets.Last());
            Assert.True(VibeSessionEpochStore.IsSerialNewer(secondEpoch, firstEpoch));
        }
    }

    [Fact]
    public async Task ExistingRandomMailboxEpochIsMigratedWithoutDeviceReset()
    {
        using var directory = new TestDirectory();
        const uint deviceEpoch = 3_690_167_845;
        var oldPacket = VibePacketEncoder.EncodeFrame(
            deviceEpoch,
            7,
            1500,
            128,
            new byte[VibePacketEncoder.RgbBytes]);
        File.WriteAllBytes(directory.File("VIBE0.BIN"), oldPacket);
        File.WriteAllBytes(directory.File("VIBE1.BIN"), oldPacket);
        File.WriteAllText(directory.File("epoch.txt"), "1780000000");

        var config = AppConfig.CreateDefault();
        config.Global.ActiveMode = PrimaryMode.SystemMonitor;
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        await using var orchestrator = Create(
            directory,
            config,
            new PicoDeviceLocation(directory.Path),
            transport,
            new VibeSessionEpochStore(directory.File("epoch.txt")));
        orchestrator.Start();

        await WaitUntilAsync(() => transport.VibePackets.Count > 0);
        Assert.Equal(unchecked(deviceEpoch + 1), EpochOf(transport.VibePackets.Last()));
    }

    [Fact]
    public async Task MissingDeviceFallsBackWithoutArmingOrWaiting()
    {
        using var directory = new TestDirectory();
        var config = AppConfig.CreateDefault();
        config.Ai.ApprovalTimeoutSeconds = 30;
        await using var orchestrator = new LightOrchestrator(
            config,
            new ConfigStore(directory.File("config.json")),
            new NullLocator(),
            new FakeTransport(),
            enableHookPipe: false,
            epochStore: new VibeSessionEpochStore(directory.File("epoch.txt")));

        var started = DateTimeOffset.UtcNow;
        var response = await orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);

        Assert.Equal(HookDecision.None, response.Decision);
        Assert.Contains("正常询问", response.Message, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateConfigAlsoTakesAnOwnedDeepSnapshot()
    {
        using var directory = new TestDirectory();
        var device = new PicoDeviceLocation(directory.Path);
        var transport = new FakeTransport();
        transport.SetTouch(0, "none", 10);
        var initial = AppConfig.CreateDefault();
        await using var orchestrator = Create(directory, initial, device, transport);
        var updated = AppConfig.CreateDefault();
        updated.Profiles["ai-idle"].Effect = RgbEffect.Solid;
        orchestrator.UpdateConfig(updated);

        updated.Global.LightsEnabled = false;
        updated.Profiles["ai-idle"].Effect = RgbEffect.Fire;
        orchestrator.Start();

        await WaitUntilAsync(() => transport.LastRgbCommand is not null);
        Assert.StartsWith("MODE=solid", transport.LastRgbCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameNamedButIncompatibleVolumeIsNeverOpenedForDeviceIo()
    {
        using var directory = new TestDirectory();
        File.Delete(directory.File("VIBE1.BIN"));
        var transport = new FakeTransport();
        var config = AppConfig.CreateDefault();
        await using var orchestrator = Create(
            directory,
            config,
            new PicoDeviceLocation(directory.Path),
            transport);

        var response = await orchestrator.HandleHookAsync(
            Request(HookEvent.PermissionRequested),
            CancellationToken.None);

        Assert.Equal(HookDecision.None, response.Decision);
        Assert.Equal(0, transport.TouchReadCount);
        Assert.Empty(transport.VibePackets);
        Assert.Null(transport.LastRgbCommand);
    }

    private static LightOrchestrator Create(
        TestDirectory directory,
        AppConfig config,
        PicoDeviceLocation device,
        FakeTransport transport,
        VibeSessionEpochStore? epochStore = null,
        ISystemMediaController? mediaController = null,
        IAudioLevelService? audioService = null) => new(
            config,
            new ConfigStore(directory.File("config.json")),
            new FixedLocator(device),
            transport,
            audioService: audioService,
            enableHookPipe: false,
            epochStore: epochStore ?? new VibeSessionEpochStore(directory.File("epoch.txt")),
            mediaController: mediaController);

    private static uint EpochOf(byte[] packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, sizeof(uint)));

    private static HookRequest Request(
        HookEvent hookEvent,
        HookPlatform platform = HookPlatform.Cursor) => new()
    {
        Platform = platform,
        Event = hookEvent,
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(4));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("Condition was not reached before the test deadline.");
    }

    private sealed class FixedLocator(PicoDeviceLocation device) : IPicoVolumeLocator
    {
        public ValueTask<PicoDeviceLocation?> FindAsync(
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PicoDeviceLocation?>(device);
    }

    private sealed class NullLocator : IPicoVolumeLocator
    {
        public ValueTask<PicoDeviceLocation?> FindAsync(
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PicoDeviceLocation?>(null);
    }

    private sealed class FakeSystemMediaController : ISystemMediaController
    {
        private int nextTrackCount;

        public int NextTrackCount => Volatile.Read(ref nextTrackCount);

        public bool SendNextTrack()
        {
            Interlocked.Increment(ref nextTrackCount);
            return true;
        }
    }

    /// <summary>Pushes levels into the orchestrator, which the shared fake
    /// cannot do because it swallows the event.</summary>
    private sealed class DrivenAudioLevelService : IAudioLevelService
    {
        public event EventHandler<AudioLevelSample>? LevelChanged;

        public AudioLevelSample Current { get; private set; } =
            AudioLevelSample.Unavailable("No level yet.");
        public bool IsRunning => true;
        public TimeSpan AttackTime { get; set; }
        public TimeSpan ReleaseTime { get; set; }

        public void Push(double peak, double rms)
        {
            var sample = AudioLevelSample.Success(peak, rms, "test");
            Current = sample;
            LevelChanged?.Invoke(this, sample);
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioLevelService : IAudioLevelService
    {
        private int startCount;
        private int stopCount;
        private int attackTimeSetCount;
        private TimeSpan attackTime;

        public event EventHandler<AudioLevelSample> LevelChanged
        {
            add { }
            remove { }
        }

        public AudioLevelSample Current { get; } =
            AudioLevelSample.Unavailable("Test audio capture is unavailable.");

        // A permission-denied backend can accept StartAsync yet remain stopped.
        public bool IsRunning => false;
        public int StartCount => Volatile.Read(ref startCount);
        public int StopCount => Volatile.Read(ref stopCount);
        public int AttackTimeSetCount => Volatile.Read(ref attackTimeSetCount);

        public TimeSpan AttackTime
        {
            get => attackTime;
            set
            {
                attackTime = value;
                Interlocked.Increment(ref attackTimeSetCount);
            }
        }

        public TimeSpan ReleaseTime { get; set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref startCount);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref stopCount);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransport : IPicoDeviceTransport
    {
        private readonly object gate = new();
        private string touchText = "seq=0\nevt=none\nt_ms=10\n";
        private int touchReadCount;
        private int resetConnectionCount;
        private int failTouchReadsUntilReset;
        private int wedgeTouchReadsUntilReset;
        private int vibeWriteDelayMilliseconds;
        private int failVibeWritesUntilReset;
        private TaskCompletionSource? blockedReadEntered;
        private TaskCompletionSource? blockedReadRelease;
        private bool blockedReadShouldFail;

        public string? LastRgbCommand { get; private set; }
        public ConcurrentQueue<byte[]> VibePackets { get; } = new();
        public ConcurrentQueue<byte[]> ReleasePackets { get; } = new();
        public int TouchReadCount => Volatile.Read(ref touchReadCount);
        public int ResetConnectionCount => Volatile.Read(ref resetConnectionCount);

        public void FailVibeWritesUntilReset() =>
            Volatile.Write(ref failVibeWritesUntilReset, 1);

        public void FailTouchReadsUntilReset() =>
            Volatile.Write(ref failTouchReadsUntilReset, 1);

        /// <summary>
        /// Models a wedged card volume: the read is accepted and simply never
        /// answers, which is what a stuck fskit/msdos volume does.
        /// </summary>
        public void WedgeTouchReadsUntilReset() =>
            Volatile.Write(ref wedgeTouchReadsUntilReset, 1);

        /// <summary>Models the real ~400 kHz link, where one 512-byte
        /// operation costs tens of milliseconds.</summary>
        public void SlowVibeWrites(int milliseconds) =>
            Volatile.Write(ref vibeWriteDelayMilliseconds, milliseconds);

        public void SetTouch(ulong sequence, string gesture, ulong milliseconds)
        {
            lock (gate)
            {
                touchText = $"seq={sequence}\nevt={gesture}\nt_ms={milliseconds}\n";
            }
        }

        public BlockedRead SetTouchAndBlockNextRead(
            ulong sequence,
            string gesture,
            ulong milliseconds)
        {
            lock (gate)
            {
                touchText = $"seq={sequence}\nevt={gesture}\nt_ms={milliseconds}\n";
                blockedReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                blockedReadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                blockedReadShouldFail = false;
                var release = blockedReadRelease;
                return new BlockedRead(blockedReadEntered.Task, () => release.TrySetResult());
            }
        }

        public BlockedRead BlockAndFailNextTouchRead()
        {
            lock (gate)
            {
                blockedReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                blockedReadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                blockedReadShouldFail = true;
                var release = blockedReadRelease;
                return new BlockedRead(blockedReadEntered.Task, () => release.TrySetResult());
            }
        }

        public ValueTask WriteRgbCommandAsync(
            PicoDeviceLocation device,
            string command,
            CancellationToken cancellationToken = default)
        {
            LastRgbCommand = command;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteLedFrameAsync(
            PicoDeviceLocation device,
            ReadOnlyMemory<byte> rgb888,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async ValueTask<string?> ReadTouchTextAsync(
            PicoDeviceLocation device,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref touchReadCount);
            if (Volatile.Read(ref failTouchReadsUntilReset) != 0)
            {
                throw new IOException("Injected remounted touch volume.");
            }
            if (Volatile.Read(ref wedgeTouchReadsUntilReset) != 0)
            {
                // Deliberately ignores the token: a blocked file syscall on a
                // wedged volume cannot be cancelled either.
                await Task.Delay(Timeout.Infinite, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30));
            }
            string snapshot;
            TaskCompletionSource? entered;
            TaskCompletionSource? release;
            bool failAfterRelease;
            lock (gate)
            {
                snapshot = touchText;
                entered = blockedReadEntered;
                release = blockedReadRelease;
                failAfterRelease = blockedReadShouldFail;
                blockedReadEntered = null;
                blockedReadRelease = null;
                blockedReadShouldFail = false;
            }
            entered?.TrySetResult();
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            if (failAfterRelease)
            {
                throw new IOException("Injected stale touch read after reconnect started.");
            }
            return snapshot;
        }

        public ValueTask DeleteLedFrameAsync(
            PicoDeviceLocation device,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async ValueTask WriteVibePacketAsync(
            PicoDeviceLocation device,
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref failVibeWritesUntilReset) != 0)
            {
                throw new IOException("Injected stale volume handle.");
            }
            var delay = Volatile.Read(ref vibeWriteDelayMilliseconds);
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            VibePackets.Enqueue(packet.ToArray());
        }

        public ValueTask ReleaseVibeAsync(
            PicoDeviceLocation device,
            ReadOnlyMemory<byte> releasePacket,
            CancellationToken cancellationToken = default)
        {
            ReleasePackets.Enqueue(releasePacket.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask ResetConnectionAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref resetConnectionCount);
            Volatile.Write(ref failTouchReadsUntilReset, 0);
            Volatile.Write(ref failVibeWritesUntilReset, 0);
            Volatile.Write(ref wedgeTouchReadsUntilReset, 0);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public sealed record BlockedRead(Task Entered, Action Release);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PicoVibe.App.Tests",
                Guid.NewGuid().ToString("N"));
            CreateDeviceFiles();
        }

        public void CreateDeviceFiles()
        {
            Directory.CreateDirectory(Path);
            System.IO.File.WriteAllBytes(File("VIBE0.BIN"), new byte[VibePacketEncoder.SectorSize]);
            System.IO.File.WriteAllBytes(File("VIBE1.BIN"), new byte[VibePacketEncoder.SectorSize]);
            System.IO.File.WriteAllText(File("TOUCH.TXT"), "seq=0\nevt=none\nt_ms=0\n");
        }

        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PicoVibe.App.Tests"));
            var target = System.IO.Path.GetFullPath(Path);
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}
