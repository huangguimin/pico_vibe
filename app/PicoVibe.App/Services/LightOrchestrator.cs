using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using PicoVibe.Core.Configuration;
using PicoVibe.Core.Device;
using PicoVibe.Core.Ipc;
using PicoVibe.Core.Lighting;
using PicoVibe.Core.Touch;

namespace PicoVibe.App.Services;

public sealed record DeviceStatus(bool Connected, string Message, string? RootPath = null);

public sealed record TouchDispatch(TouchGesture Gesture, TouchAction Action, TouchContext Context);

/// <summary>
/// The card volume accepted an operation and never answered. It is an
/// <see cref="IOException"/> so every existing device-failure path keeps
/// working, and a distinct type so the user can be told the difference
/// between a failed write and a device that stopped responding.
/// </summary>
public sealed class DeviceUnresponsiveException : IOException
{
    public DeviceUnresponsiveException(string message) : base(message)
    {
    }
}

/// <summary>
/// The single resident broker for device I/O. Hooks never race each other for
/// the RAM disk while the application is running, and a touch is dispatched
/// only against the context that was active when the firmware reported it.
/// </summary>
public sealed partial class LightOrchestrator : IAsyncDisposable
{
    private static readonly TimeSpan TouchPollInterval = OperatingSystem.IsMacOS()
        ? TimeSpan.FromMilliseconds(250)
        : TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DynamicLease = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DynamicLeaseRefresh = TimeSpan.FromMilliseconds(650);
    // A wedged card volume never fails a write, it simply never answers, so a
    // bounded wait is the only way the broker can notice and rediscover it.
    private static readonly TimeSpan DeviceIoTimeout = TimeSpan.FromSeconds(2);
    // The card runs at ~400 kHz by design (see the SDSC rationale in
    // sdio_card.c: no response sent at 25 MHz has ever been accepted), so one
    // 512-byte operation costs tens of milliseconds and the whole link carries
    // only a few dozen a second. Spending it all on frames starves the touch
    // poll and every filesystem access the OS makes, which is what turns a slow
    // link into "the disk stopped responding". Leave most of it unspent.
    private const double LinkDutyFactor = 3.0;
    private const double LinkCostSmoothing = 0.2;

    private readonly ConfigStore configStore;
    private readonly IPicoVolumeLocator locator;
    private readonly IPicoDeviceTransport transport;
    private readonly ISystemMetricService metricService;
    private readonly IAudioLevelService audioService;
    private readonly ISystemMediaController mediaController;
    private readonly TouchEventDeduplicator touchDeduplicator = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim permissionGate = new(1, 1);
    private readonly SemaphoreSlim touchProcessingGate = new(1, 1);
    private readonly SemaphoreSlim deviceIoGate = new(1, 1);
    private readonly SemaphoreSlim deviceDiscoveryGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly VibeSessionEpochStore epochStore;
    private readonly MusicAutoGainController musicAutoGain = new();
    private readonly SleepGuard sleepGuard = new();
    private readonly bool enableHookPipe;

    private AppConfig config;
    private HookPipeServer? pipeServer;
    private Task? deviceTask;
    private Task? touchTask;
    private Task? lightingTask;
    private Task? metricTask;
    private Task? audioModeTask;
    private PicoDeviceLocation? device;
    private PendingPermission? pendingPermission;
    private LightProfile? previewProfile;
    private DateTimeOffset previewUntil;
    private AiState aiState = AiState.Idle;
    private readonly AiStateTracker aiTracker = new();
    private readonly HookEventJournal eventJournal;
    private Task? eventRecoveryTask;
    private bool manualAiSelection;
    private MetricSample latestMetric = MetricSample.Unavailable("等待首次采样。", "Pico Vibe");
    private SystemMetric latestMetricKind = SystemMetric.CpuUsage;
    private AudioLevelSample latestAudio = AudioLevelSample.Unavailable("等待音频设备。");
    private AudioLevels latestMusicLevels = AudioLevels.FromPeakAndRms(0, 0);
    private bool musicPaused;
    private uint sessionEpoch;
    private uint sequence;
    private string? lastRgbCommand;
    private bool vibeActive;
    private DateTimeOffset lastVibeWrite;
    private double linkOperationMilliseconds;
    private DateTimeOffset lastPacingReport;
    private PicoDeviceInfo deviceInfo = PicoDeviceInfo.Legacy;
    private long deviceGeneration;
    private int startState;
    private int disposeState;

    public LightOrchestrator(
        AppConfig initialConfig,
        ConfigStore? configStore = null,
        IPicoVolumeLocator? locator = null,
        IPicoDeviceTransport? transport = null,
        ISystemMetricService? metricService = null,
        IAudioLevelService? audioService = null,
        bool enableHookPipe = true,
        VibeSessionEpochStore? epochStore = null,
        ISystemMediaController? mediaController = null)
    {
        ArgumentNullException.ThrowIfNull(initialConfig);
        config = CloneConfig(initialConfig);
        this.configStore = configStore ?? new ConfigStore();
        eventJournal = new HookEventJournal(Path.Combine(Path.GetDirectoryName(this.configStore.FilePath)!, "hook-events"), DateTimeOffset.UtcNow);
        InitializeAgentLamps();
        this.locator = locator ?? new PicoRamLocator();
        this.transport = transport ?? new PicoFileTransport();
        this.metricService = metricService ?? new UnavailableSystemMetricService();
        this.audioService = audioService ?? new UnavailableAudioLevelService();
        this.mediaController = mediaController ?? new NullSystemMediaController();
        this.enableHookPipe = enableHookPipe;
        this.epochStore = epochStore ?? new VibeSessionEpochStore();
        sessionEpoch = this.epochStore.AllocateNext();
        this.audioService.LevelChanged += OnAudioLevelChanged;
    }

    public event EventHandler<DeviceStatus>? DeviceStatusChanged;
    public event EventHandler<TouchDispatch>? TouchDispatched;
    public event EventHandler<MetricReading>? MetricChanged;
    public event EventHandler<AudioLevelSample>? AudioLevelChanged;
    public event EventHandler? ShowRequested;
    public event EventHandler? ShutdownRequested;
    public event EventHandler<AppConfig>? ConfigChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        if (Interlocked.CompareExchange(ref startState, 1, 0) != 0)
        {
            return;
        }

        if (enableHookPipe)
        {
            pipeServer = new HookPipeServer(HandleHookAsync);
            pipeServer.Start();
        }
        var token = shutdown.Token;
        deviceTask = Task.Run(() => DeviceLoopAsync(token), token);
        touchTask = Task.Run(() => TouchLoopAsync(token), token);
        lightingTask = Task.Run(() => LightingLoopAsync(token), token);
        metricTask = Task.Run(() => MetricLoopAsync(token), token);
        audioModeTask = Task.Run(() => AudioModeLoopAsync(token), token);
        eventRecoveryTask = Task.Run(() => RecoverEventsAsync(token), token);
        AppLog.Info("Resident broker started.");
    }

    public void UpdateConfig(AppConfig updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (stateGate)
        {
            config = CloneConfig(updated);
            if (!config.Ai.Enabled || !config.Touch.Enabled ||
                (pendingPermission?.Request.Platform != HookPlatform.Cursor && !config.Ai.CodexTouchApproval))
                pendingPermission?.Completion.TrySetResult(HookDecision.None);
            if (pendingPermission is { } pending && config.Ai.DisplayMode == AiDisplayMode.PerAgent &&
                !UpdateAgentLampsLocked(DateTimeOffset.UtcNow).Any(l => l.Agent.Key == AgentSnapshot.KeyFor(pending.Request) && l.Slot is not null))
                pending.Completion.TrySetResult(HookDecision.None);
            lastRgbCommand = null;
        }
    }

    public Task ForceReconnectAsync(CancellationToken cancellationToken = default) =>
        DisconnectAndResetTransportAsync(
            GetDeviceConnection(),
            "正在重新检测设备",
            cancellationToken);

    public void Preview(LightProfile profile, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (stateGate)
        {
            previewProfile = CloneProfile(profile);
            previewUntil = DateTimeOffset.UtcNow + (duration ?? TimeSpan.FromSeconds(3));
            lastRgbCommand = null;
        }
    }

    public async Task<HookResponse> HandleHookAsync(
        HookRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Metadata.TryGetValue("control", out var control))
        {
            if (string.Equals(control, "show", StringComparison.OrdinalIgnoreCase))
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
                return HookResponse.NoDecision(request, "Application shown.");
            }

            if (string.Equals(control, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                ShutdownRequested?.Invoke(this, EventArgs.Empty);
                return HookResponse.NoDecision(request, "Application shutdown requested.");
            }
        }

        AppConfig snapshot = GetConfig();
        if (!snapshot.Ai.Enabled)
        {
            return HookResponse.NoDecision(request, "AI integration is disabled.");
        }

        lock (stateGate)
        {
            if (aiTracker.Apply(request)) manualAiSelection = false;
            if (request.Event is HookEvent.AgentStopped or HookEvent.SessionEnded &&
                pendingPermission is { } pending && pending.Request.Platform == request.Platform &&
                pending.Request.SessionId == request.SessionId &&
                (request.Event == HookEvent.SessionEnded ||
                 (pending.Request.TurnId == request.TurnId && pending.Request.AgentId == request.AgentId)))
                pending.Completion.TrySetResult(HookDecision.None);
            lastRgbCommand = null;
        }
        if (request.Event == HookEvent.PermissionRequested && !request.ObserveOnly &&
            snapshot.Touch.Enabled && (request.Platform == HookPlatform.Cursor || snapshot.Ai.CodexTouchApproval))
        {
            return await WaitForPermissionAsync(request, snapshot, cancellationToken).ConfigureAwait(false);
        }
        return HookResponse.NoDecision(request);
    }

    private async Task RecoverEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var recovered = eventJournal.ReadNew(DateTimeOffset.UtcNow);
                lock (stateGate)
                {
                    // Only events since this broker started are recovered.
                    // Historical diagnostics never resurrect online sessions.
                    if (pendingPermission is { } pending &&
                        pending.Request.OwnerProcessId is { } ownerPid &&
                        pending.Request.OwnerProcessStartUtcTicks is { } ownerStart &&
                        AgentProcessIdentity.IsAlive(ownerPid, ownerStart) == false)
                        pending.Completion.TrySetResult(HookDecision.None);
                    foreach (var request in recovered)
                        if (aiTracker.Apply(request))
                        {
                            manualAiSelection = false;
                            lastRgbCommand = null;
                        }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Reading AI status recovery events failed.", exception);
            }
            await PersistAgentLampsAsync(cancellationToken).ConfigureAwait(false);
            await DelaySafeAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HookResponse> WaitForPermissionAsync(
        HookRequest request,
        AppConfig snapshot,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            shutdown.Token);
        await permissionGate.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
        try
        {
            var current = await EnsureDeviceAvailableAsync(waitCancellation.Token).ConfigureAwait(false);
            if (current is null)
            {
                return HookResponse.NoDecision(
                    request,
                    "未找到 PICO_RGB，已回到 AI 自带的正常询问流程。");
            }

            lock (stateGate)
            {
                if (config.Ai.DisplayMode == AiDisplayMode.PerAgent &&
                    !UpdateAgentLampsLocked(DateTimeOffset.UtcNow).Any(l => l.Agent.Key == AgentSnapshot.KeyFor(request) && l.Slot is not null))
                    return HookResponse.NoDecision(request, "该 Agent 尚未分配灯位，请在 AI 客户端处理授权。");
            }

            var completion = new TaskCompletionSource<HookDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingPermission(request, completion);
            var armed = await TryArmPermissionAsync(current, pending, waitCancellation.Token)
                .ConfigureAwait(false);
            if (!armed)
            {
                return HookResponse.NoDecision(
                    request,
                    "触摸设备当前不可用，已回到 AI 自带的正常询问流程。");
            }

            var source = string.IsNullOrWhiteSpace(request.ToolName)
                ? request.Platform.ToString()
                : $"{request.Platform}/{request.ToolName}";
            var waitStarted = Stopwatch.GetTimestamp();
            var outcome = "client canceled";
            AppLog.Info(
                $"Permission request {request.RequestId} from {source} is waiting " +
                $"for a new touch ({snapshot.Ai.ApprovalTimeoutSeconds}s window).");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(snapshot.Ai.ApprovalTimeoutSeconds));
            try
            {
                var decision = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (decision == HookDecision.None)
                {
                    outcome = "device disconnected";
                    return HookResponse.NoDecision(
                        request,
                        "触摸设备已断开，已回到 AI 自带的正常询问流程。");
                }
                outcome = decision == HookDecision.Allow ? "allowed by touch" : "denied by touch";
                return new HookResponse
                {
                    RequestId = request.RequestId,
                    Decision = decision,
                    Message = decision == HookDecision.Deny
                        ? "用户已通过触摸拒绝此请求。"
                        : "用户已通过触摸批准此请求。",
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
            {
                outcome = "timed out";
                return HookResponse.NoDecision(request, "触摸授权超时，已回到 AI 自带的正常询问流程。");
            }
            finally
            {
                lock (stateGate)
                {
                    if (ReferenceEquals(pendingPermission, pending))
                    {
                        pendingPermission = null;
                        lastRgbCommand = null;
                    }
                }
                AppLog.Info(
                    $"Permission request {request.RequestId} from {source} ended after " +
                    $"{Stopwatch.GetElapsedTime(waitStarted).TotalSeconds:F1}s: {outcome}.");
            }
        }
        finally
        {
            permissionGate.Release();
        }
    }

    private async Task<bool> TryArmPermissionAsync(
        PicoDeviceLocation expectedDevice,
        PendingPermission pending,
        CancellationToken cancellationToken)
    {
        var connection = GetDeviceConnection();
        if (!ReferenceEquals(connection.Device, expectedDevice))
        {
            return false;
        }

        await touchProcessingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? text = null;
            var connectionFailed = false;
            var stale = false;
            try
            {
                await RunDeviceIoAsync(async token =>
                {
                    if (!IsCurrentConnection(connection))
                    {
                        stale = true;
                        return;
                    }
                    text = await transport.ReadTouchTextAsync(expectedDevice, token)
                        .ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Touch baseline read failed while arming permission.", exception);
                connectionFailed = true;
            }

            if (stale)
            {
                return false;
            }

            if (connectionFailed)
            {
                await DisconnectAndResetTransportAsync(
                    connection,
                    "设备读写中断，正在重连",
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (!TouchEventParser.TryParse(text, out var baseline))
            {
                return false;
            }

            lock (stateGate)
            {
                if (!IsCurrentConnectionLocked(connection))
                {
                    return false;
                }

                if (!config.Ai.Enabled || !config.Touch.Enabled ||
                    (pending.Request.Platform != HookPlatform.Cursor && !config.Ai.CodexTouchApproval) ||
                    (config.Ai.DisplayMode == AiDisplayMode.PerAgent &&
                     !UpdateAgentLampsLocked(DateTimeOffset.UtcNow).Any(l => l.Agent.Key == AgentSnapshot.KeyFor(pending.Request) && l.Slot is not null)))
                    return false;

                // Consume everything already present before arming. TouchLoop
                // uses this same gate, so an observed pre-request gesture can
                // never be reclassified later as a Permission gesture.
                _ = touchDeduplicator.Observe(expectedDevice.DeviceId, baseline);
                pendingPermission = pending;
                lastRgbCommand = null;
                return true;
            }
        }
        finally
        {
            touchProcessingGate.Release();
        }
    }

    private async Task<PicoDeviceLocation?> EnsureDeviceAvailableAsync(
        CancellationToken cancellationToken)
    {
        var connection = GetDeviceConnection();
        var current = connection.Device;
        if (current is not null && PicoDeviceCompatibility.IsAccessible(current))
        {
            return current;
        }

        await deviceDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            connection = GetDeviceConnection();
            current = connection.Device;
            if (current is not null && PicoDeviceCompatibility.IsAccessible(current))
            {
                return current;
            }

            if (current is not null)
            {
                await DisconnectAndResetTransportAsync(
                    connection,
                    "未找到 PICO_RGB",
                    cancellationToken).ConfigureAwait(false);
            }

            var preferredPath = Environment.GetEnvironmentVariable("PICO_RGB_PATH");
            if (string.IsNullOrWhiteSpace(preferredPath))
            {
                preferredPath = Environment.GetEnvironmentVariable("PICO_RAM_PATH");
            }
            var found = await locator.FindAsync(preferredPath, cancellationToken)
                .ConfigureAwait(false);
            if (found is null)
            {
                return null;
            }

            if (!PicoDeviceCompatibility.IsSupported(found))
            {
                SetDevice(null, "发现同名磁盘，但不是兼容的 Pico Vibe 固件");
                return null;
            }

            await PrepareDeviceEpochAsync(found, cancellationToken).ConfigureAwait(false);
            var info = await ReadDeviceInfoAsync(found, cancellationToken).ConfigureAwait(false);
            lock (stateGate)
            {
                deviceInfo = info;
            }
            AppLog.Info($"Device identified: {info.Describe()}.");
            if (info.LedCount != VibePacketEncoder.LedCount)
            {
                // The VIBE frame is a fixed 12-lamp payload, so a board that
                // declares a different count needs a protocol revision, not a
                // silent reinterpretation of the same bytes.
                AppLog.Info(
                    $"The device declares {info.LedCount} lamps but the VIBE frame carries " +
                    $"{VibePacketEncoder.LedCount}; lighting stays on the frame layout.");
            }
            SetDevice(found, $"已连接 {found.RootPath}");
            return found;
        }
        finally
        {
            deviceDiscoveryGate.Release();
        }
    }

    private async Task DeviceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var current = GetDevice();
                if (current is null || !PicoDeviceCompatibility.IsAccessible(current))
                {
                    _ = await EnsureDeviceAvailableAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Error("Device detection failed.", exception);
                await DisconnectAndResetTransportAsync(
                    GetDeviceConnection(),
                    "设备检测失败，正在重试",
                    cancellationToken).ConfigureAwait(false);
            }

            await DelaySafeAsync(DevicePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TouchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connection = GetDeviceConnection();
            var current = connection.Device;
            if (current is not null)
            {
                try
                {
                    TouchDispatchPlan? plan = null;
                    await touchProcessingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        string? text = null;
                        await RunDeviceIoAsync(async token =>
                        {
                            text = IsCurrentConnection(connection)
                                ? await transport.ReadTouchTextAsync(current, token)
                                    .ConfigureAwait(false)
                                : null;
                        }, cancellationToken).ConfigureAwait(false);

                        if (TouchEventParser.TryParse(text, out var touch))
                        {
                            lock (stateGate)
                            {
                                if (IsCurrentConnectionLocked(connection))
                                {
                                    var observation = touchDeduplicator.Observe(current.DeviceId, touch);
                                    if (observation.ShouldDispatch && touch.Gesture != TouchGesture.None)
                                    {
                                        plan = CreateTouchDispatchPlanLocked(touch.Gesture);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        touchProcessingGate.Release();
                    }

                    if (plan is not null)
                    {
                        await ExecuteTouchDispatchAsync(plan).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (DeviceUnresponsiveException exception)
                {
                    AppLog.Error("Device stopped answering touch reads; it will be rediscovered.", exception);
                    await DisconnectAndResetTransportAsync(
                        connection,
                        "设备无响应，正在重连（可能需要重新插拔）",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("Touch read failed; device will be rediscovered.", exception);
                    await DisconnectAndResetTransportAsync(
                        connection,
                        "设备读写中断，正在重连",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await DelaySafeAsync(TouchPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private TouchDispatchPlan CreateTouchDispatchPlanLocked(TouchGesture gesture)
    {
        var snapshot = config;
        var pending = pendingPermission;
        var context = pending is not null
            ? TouchContext.Permission
            : snapshot.Global.ActiveMode switch
            {
                PrimaryMode.SystemMonitor => TouchContext.SystemMonitor,
                PrimaryMode.Music => TouchContext.Music,
                _ => TouchContext.Normal,
            };
        var action = snapshot.Touch.Enabled && snapshot.Touch.Contexts.TryGetValue(context, out var bindings)
            ? bindings.For(gesture)
            : TouchAction.None;
        return new TouchDispatchPlan(gesture, action, context, pending);
    }

    private async Task ExecuteTouchDispatchAsync(TouchDispatchPlan plan)
    {
        if (plan.Pending is not null)
        {
            if (plan.Action == TouchAction.AllowRequest)
            {
                plan.Pending.Completion.TrySetResult(HookDecision.Allow);
            }
            else if (plan.Action is TouchAction.DenyRequest or TouchAction.CancelRequest)
            {
                plan.Pending.Completion.TrySetResult(HookDecision.Deny);
            }
        }
        else
        {
            await ApplyNormalTouchActionAsync(plan.Action).ConfigureAwait(false);
        }

        var permissionSource = plan.Pending is null
            ? string.Empty
            : $" for {plan.Pending.Request.Platform}";
        AppLog.Info(
            $"Touch {plan.Gesture} dispatched as {plan.Action} in {plan.Context} context{permissionSource}.");
        TouchDispatched?.Invoke(this, new TouchDispatch(plan.Gesture, plan.Action, plan.Context));
    }

    private async Task ApplyNormalTouchActionAsync(TouchAction action)
    {
        AppConfig? changedSnapshot = null;
        var showApplication = false;
        var sendNextTrack = false;
        lock (stateGate)
        {
            AppConfig? updated = null;
            switch (action)
            {
                case TouchAction.NextPreset:
                    aiState = NextEnum(manualAiSelection ? aiState : aiTracker.GetState(DateTimeOffset.UtcNow, config.Ai), AiState.Permission, forward: true);
                    manualAiSelection = true;
                    lastRgbCommand = null;
                    break;
                case TouchAction.PreviousPreset:
                    aiState = NextEnum(manualAiSelection ? aiState : aiTracker.GetState(DateTimeOffset.UtcNow, config.Ai), AiState.Permission, forward: false);
                    manualAiSelection = true;
                    lastRgbCommand = null;
                    break;
                case TouchAction.ToggleLights:
                    updated = CloneConfig(config);
                    updated.Global.LightsEnabled = !updated.Global.LightsEnabled;
                    lastRgbCommand = null;
                    break;
                case TouchAction.CyclePrimaryMode:
                    updated = CloneConfig(config);
                    updated.Global.ActiveMode = NextEnum(updated.Global.ActiveMode, null, forward: true);
                    lastRgbCommand = null;
                    break;
                case TouchAction.NextMetric:
                    updated = CloneConfig(config);
                    updated.SystemMonitor.Metric = NextEnum(updated.SystemMonitor.Metric, null, forward: true);
                    break;
                case TouchAction.NextVisualizer:
                    updated = CloneConfig(config);
                    if (updated.Global.ActiveMode == PrimaryMode.Music)
                    {
                        updated.Music.Visualizer = NextEnum(updated.Music.Visualizer, null, forward: true);
                    }
                    else
                    {
                        updated.SystemMonitor.Visualizer = NextEnum(updated.SystemMonitor.Visualizer, null, forward: true);
                    }
                    break;
                case TouchAction.TogglePause:
                    musicPaused = !musicPaused;
                    break;
                case TouchAction.NextTrack:
                    sendNextTrack = true;
                    break;
                case TouchAction.ShowApplication:
                    showApplication = true;
                    break;
            }

            if (updated is not null)
            {
                config = AppConfigDefaults.Normalize(updated);
                changedSnapshot = config;
            }
        }

        if (showApplication)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }

        if (sendNextTrack)
        {
            try
            {
                if (!mediaController.SendNextTrack())
                {
                    AppLog.Info("The system did not accept the next-track media command.");
                }
            }
            catch (Exception exception)
            {
                AppLog.Error("Sending the next-track media command failed.", exception);
            }
        }

        if (changedSnapshot is not null)
        {
            var eventSnapshot = CloneConfig(changedSnapshot);
            ConfigChanged?.Invoke(this, eventSnapshot);
            try
            {
                await configStore.SaveAsync(CloneConfig(changedSnapshot)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLog.Error("Saving a touch-triggered setting failed.", exception);
            }
        }
    }

    private async Task LightingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connection = GetDeviceConnection();
            var current = connection.Device;
            ApplySleepGuard(current is not null);
            if (current is not null)
            {
                try
                {
                    await RenderCurrentOutputAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (DeviceUnresponsiveException exception)
                {
                    AppLog.Error("Device stopped responding; it will be rediscovered.", exception);
                    await DisconnectAndResetTransportAsync(
                        connection,
                        "设备无响应，正在重连（可能需要重新插拔）",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    AppLog.Error("Lighting write failed; device will be rediscovered.", exception);
                    await DisconnectAndResetTransportAsync(
                        connection,
                        "设备写入失败，正在重连",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await DelaySafeAsync(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }

    private static RgbColor Midpoint(RgbColor from, RgbColor to) =>
        new((from.R + to.R) / 2, (from.G + to.G) / 2, (from.B + to.B) / 2);

    /// <summary>What the connected device reports about itself.</summary>
    public PicoDeviceInfo DeviceInfo
    {
        get
        {
            lock (stateGate)
            {
                return deviceInfo;
            }
        }
    }

    /// <summary>
    /// Firmware from before INFO.TXT existed simply has no such file, which is
    /// an older build rather than an incompatible device.
    /// </summary>
    private async Task<PicoDeviceInfo> ReadDeviceInfoAsync(
        PicoDeviceLocation device,
        CancellationToken cancellationToken)
    {
        try
        {
            string? text = null;
            await RunDeviceIoAsync(async token =>
            {
                if (File.Exists(device.InfoPath))
                {
                    text = await File.ReadAllTextAsync(device.InfoPath, token).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
            return PicoDeviceInfo.Parse(text);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Reading the device info file failed; assuming an older firmware.", exception);
            return PicoDeviceInfo.Legacy;
        }
    }

    private void NoteLinkCost(TimeSpan elapsed)
    {
        var sample = elapsed.TotalMilliseconds;
        lock (stateGate)
        {
            linkOperationMilliseconds = linkOperationMilliseconds <= 0
                ? sample
                : (linkOperationMilliseconds * (1 - LinkCostSmoothing)) +
                  (sample * LinkCostSmoothing);
        }
    }

    /// <summary>
    /// Never ask the card for frames faster than it has been answering. The
    /// requested rate is a ceiling, not a promise: on a degraded link, issuing
    /// frames the device cannot drain builds a backlog that stalls every other
    /// access to the volume for minutes.
    /// </summary>
    private TimeSpan PaceForLink(TimeSpan requested)
    {
        double cost;
        lock (stateGate)
        {
            cost = linkOperationMilliseconds;
        }
        if (cost <= 0)
        {
            return requested;
        }

        var paced = TimeSpan.FromMilliseconds(cost * LinkDutyFactor);
        if (paced <= requested)
        {
            return requested;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastPacingReport > TimeSpan.FromSeconds(60))
        {
            lastPacingReport = now;
            AppLog.Info(
                $"Device writes are taking {cost:F0} ms; pacing dynamic frames at " +
                $"{1000.0 / paced.TotalMilliseconds:F1}/s instead of " +
                $"{1000.0 / requested.TotalMilliseconds:F1}/s to leave the link usable.");
        }
        return paced;
    }

    /// <summary>
    /// Runs one device operation as the single writer, and gives up waiting if
    /// the volume stops answering. A blocked file syscall cannot be cancelled,
    /// so the abandoned work keeps the writer gate until it finally returns.
    /// </summary>
    /// <summary>
    /// The board is powered by the card slot, so an idle sleep drops the volume
    /// and only a physical re-plug brings it back. Hold off idle sleep while a
    /// device is connected and the user asked for it.
    /// </summary>
    private void ApplySleepGuard(bool deviceConnected)
    {
        var snapshot = GetConfig();
        sleepGuard.Apply(
            deviceConnected &&
            snapshot.Global.PreventSleepWhileLit &&
            snapshot.Global.LightsEnabled);
    }

    private async Task RunDeviceIoAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await deviceIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var releaseHere = true;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var work = operation(cancellationToken);
            try
            {
                await work.WaitAsync(DeviceIoTimeout, cancellationToken).ConfigureAwait(false);
                NoteLinkCost(Stopwatch.GetElapsedTime(started));
            }
            catch (TimeoutException)
            {
                releaseHere = false;
                _ = work.ContinueWith(
                    static (completed, state) =>
                    {
                        // Observe the abandoned failure so it never surfaces as
                        // an unobserved task exception.
                        _ = completed.Exception;
                        ((SemaphoreSlim)state!).Release();
                    },
                    deviceIoGate,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new DeviceUnresponsiveException(
                    $"设备在 {DeviceIoTimeout.TotalSeconds:0.#} 秒内没有响应。");
            }
        }
        finally
        {
            if (releaseHere)
            {
                deviceIoGate.Release();
            }
        }
    }

    private async Task RenderCurrentOutputAsync(
        DeviceConnection connection,
        CancellationToken cancellationToken)
    {
        var current = connection.Device;
        if (current is null)
        {
            return;
        }

        AppConfig snapshot;
        LightProfile? selectedProfile = null;
        byte[]? agentFrame = null;
        PrimaryMode mode;
        var now = DateTimeOffset.UtcNow;
        lock (stateGate)
        {
            snapshot = config;
            mode = snapshot.Global.ActiveMode;
            if (snapshot.Ai.Enabled && snapshot.Ai.DisplayMode == AiDisplayMode.PerAgent &&
                (mode == PrimaryMode.Ai || (snapshot.Global.AlwaysShowAiPermission &&
                    (pendingPermission is not null || aiTracker.GetState(now, snapshot.Ai) == AiState.Permission))))
            {
                var lamps = UpdateAgentLampsLocked(now);
                agentFrame = AgentFrameRenderer.Render(lamps, snapshot.Ai.MultiAgent, snapshot.Global, now,
                    AgentLampCapacityLocked(), now < locateUntil ? locateAgentKey : null);
            }
            else if ((pendingPermission is not null ||
                 (snapshot.Ai.Enabled && aiTracker.GetState(now, snapshot.Ai) == AiState.Permission)) &&
                (mode == PrimaryMode.Ai || snapshot.Global.AlwaysShowAiPermission))
            {
                selectedProfile = ProfileFor(snapshot, AiState.Permission);
                if (pendingPermission is null)
                {
                    selectedProfile = CloneProfile(selectedProfile);
                    selectedProfile.Effect = RgbEffect.Solid;
                }
            }
            else
            {
                if (previewProfile is not null && now < previewUntil)
                {
                    selectedProfile = previewProfile;
                }
                else
                {
                    previewProfile = null;
                    if (mode == PrimaryMode.Ai)
                    {
                        selectedProfile = ProfileFor(snapshot, manualAiSelection
                            ? aiState : snapshot.Ai.Enabled ? aiTracker.GetState(now, snapshot.Ai) : AiState.Idle);
                    }
                }
            }
        }

        if (!snapshot.Global.LightsEnabled)
        {
            await WriteDynamicFrameAsync(connection, new byte[VibePacketEncoder.RgbBytes], 0, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (agentFrame is not null)
        {
            await WriteDynamicFrameAsync(connection, agentFrame, 255, cancellationToken, perAgentFrame: true).ConfigureAwait(false);
            return;
        }

        if (selectedProfile is not null)
        {
            await WriteFirmwareProfileAsync(connection, selectedProfile, snapshot, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (mode == PrimaryMode.SystemMonitor)
        {
            MetricSample sample;
            lock (stateGate)
            {
                sample = latestMetricKind == snapshot.SystemMonitor.Metric
                    ? latestMetric
                    : MetricSample.Unavailable("等待当前指标首次采样。", "Pico Vibe");
            }

            var profile = ProfileFor(snapshot, snapshot.SystemMonitor.ProfileId);
            var frame = sample.Available && sample.Value is { } value
                ? SystemFrameRenderer.RenderValue(
                    value,
                    snapshot.SystemMonitor.DisplayMinimum,
                    snapshot.SystemMonitor.DisplayMaximum,
                    new SystemFrameOptions
                    {
                        Visualizer = snapshot.SystemMonitor.Visualizer,
                        LowColor = profile.Primary,
                        HighColor = profile.Secondary,
                        Reverse = profile.Reverse,
                    },
                    FrameRenderContext.FromProfile(
                        VibePacketEncoder.LedCount,
                        profile,
                        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 3000) / 3000.0))
                : new byte[VibePacketEncoder.RgbBytes];
            await WriteDynamicFrameAsync(
                connection,
                frame,
                ToVibeBrightness(profile, snapshot.Global),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (mode == PrimaryMode.Music)
        {
            AudioLevelSample audio;
            AudioLevels musicLevels;
            bool paused;
            lock (stateGate)
            {
                audio = latestAudio;
                musicLevels = latestMusicLevels;
                paused = musicPaused;
            }

            var profile = ProfileFor(snapshot, snapshot.Music.ProfileId);
            var frame = !paused && audio.Available
                ? MusicFrameRenderer.Render(
                    musicLevels,
                    new MusicFrameOptions
                    {
                        Visualizer = snapshot.Music.Visualizer,
                        // A three-stop gradient of primary -> secondary ->
                        // primary is symmetric about the middle, which is not
                        // what "one colour at each end" means.  Putting the
                        // midpoint in the middle stop makes the ramp a plain
                        // primary-to-secondary blend.
                        LowColor = profile.Primary,
                        MidColor = Midpoint(profile.Primary, profile.Secondary),
                        HighColor = profile.Secondary,
                        Sensitivity = snapshot.Music.Sensitivity,
                        NoiseGate = snapshot.Music.NoiseGate,
                        Reverse = profile.Reverse,
                    },
                    FrameRenderContext.FromProfile(
                        VibePacketEncoder.LedCount,
                        profile,
                        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 5000) / 5000.0))
                : new byte[VibePacketEncoder.RgbBytes];
            await WriteDynamicFrameAsync(
                connection,
                frame,
                ToVibeBrightness(profile, snapshot.Global),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteFirmwareProfileAsync(
        DeviceConnection connection,
        LightProfile profile,
        AppConfig snapshot,
        CancellationToken cancellationToken)
    {
        var current = connection.Device;
        if (current is null)
        {
            return;
        }

        await RunDeviceIoAsync(async token =>
        {
            if (!IsCurrentConnection(connection))
            {
                return;
            }

            if (vibeActive)
            {
                await transport.ReleaseVibeAsync(
                    current,
                    VibePacketEncoder.EncodeRelease(sessionEpoch, NextSequence()),
                    token).ConfigureAwait(false);
                vibeActive = false;
            }

            var command = RgbIniCommandBuilder.Build(profile, snapshot.Global);
            if (!string.Equals(command, lastRgbCommand, StringComparison.Ordinal))
            {
                await transport.WriteRgbCommandAsync(current, command, token).ConfigureAwait(false);
                lastRgbCommand = command;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteDynamicFrameAsync(
        DeviceConnection connection,
        byte[] frame,
        byte brightness,
        CancellationToken cancellationToken,
        bool perAgentFrame = false)
    {
        var current = connection.Device;
        if (current is null)
        {
            return;
        }

        var snapshot = GetConfig();
        var requested = RequestedFrameInterval(snapshot, perAgentFrame);
        var interval = PaceForLink(requested);
        var now = DateTimeOffset.UtcNow;
        if (vibeActive && now - lastVibeWrite < interval && now - lastVibeWrite < DynamicLeaseRefresh)
        {
            return;
        }

        await RunDeviceIoAsync(async token =>
        {
            if (!IsCurrentConnection(connection))
            {
                return;
            }

            var packet = VibePacketEncoder.EncodeFrame(
                sessionEpoch,
                NextSequence(),
                (uint)DynamicLease.TotalMilliseconds,
                brightness,
                frame);
            await transport.WriteVibePacketAsync(current, packet, token).ConfigureAwait(false);
            vibeActive = true;
            lastRgbCommand = null;
            lastVibeWrite = now;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task MetricLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = GetConfig();
            try
            {
                var metric = snapshot.SystemMonitor.Metric;
                var sample = await metricService.GetSampleAsync(metric, cancellationToken)
                    .ConfigureAwait(false);
                bool isCurrentMetric;
                lock (stateGate)
                {
                    isCurrentMetric = config.SystemMonitor.Metric == metric;
                    if (isCurrentMetric)
                    {
                        latestMetric = sample;
                        latestMetricKind = metric;
                    }
                }
                if (!isCurrentMetric) continue;
                MetricChanged?.Invoke(this, new MetricReading(metric, sample));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Error("Metric sampling failed.", exception);
            }

            await DelaySafeAsync(
                TimeSpan.FromMilliseconds(Math.Clamp(snapshot.SystemMonitor.UpdateIntervalMilliseconds, 100, 5000)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AudioModeLoopAsync(CancellationToken cancellationToken)
    {
        bool audioRequested = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = GetConfig();
                    if (snapshot.Global.ActiveMode == PrimaryMode.Music)
                    {
                        audioService.AttackTime = TimeSpan.FromMilliseconds(15 + (150 * snapshot.Music.Attack));
                        audioService.ReleaseTime = TimeSpan.FromMilliseconds(60 + (700 * snapshot.Music.Release));
                        if (!audioRequested)
                        {
                            await audioService.StartAsync(cancellationToken).ConfigureAwait(false);
                            audioRequested = true;
                        }
                    }
                    else if (audioRequested)
                    {
                        await audioService.StopAsync().ConfigureAwait(false);
                        audioRequested = false;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    AppLog.Error("Audio lifecycle update failed.", exception);
                }

                await DelaySafeAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (audioRequested)
            {
                try { await audioService.StopAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    AppLog.Error("Stopping audio monitoring failed.", exception);
                }
            }
        }
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelSample sample)
    {
        lock (stateGate)
        {
            latestAudio = sample;
            if (sample.Available)
            {
                latestMusicLevels = musicAutoGain.Process(
                    sample.Peak,
                    sample.Rms,
                    sample.Timestamp);
            }
            else
            {
                musicAutoGain.Reset();
                latestMusicLevels = AudioLevels.FromPeakAndRms(0, 0);
            }
        }
        AudioLevelChanged?.Invoke(this, sample);
    }

    private AppConfig GetConfig()
    {
        lock (stateGate)
        {
            return config;
        }
    }

    private PicoDeviceLocation? GetDevice()
    {
        lock (stateGate)
        {
            return device;
        }
    }

    private DeviceConnection GetDeviceConnection()
    {
        lock (stateGate)
        {
            return new DeviceConnection(device, deviceGeneration);
        }
    }

    private async Task DisconnectAndResetTransportAsync(
        DeviceConnection expected,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            // A wedged volume can leave an abandoned operation holding the
            // writer gate. Closing the cached handles is exactly what has to
            // happen then, so take the gate if it is free and continue without
            // it rather than waiting on I/O that may never return.
            var owned = await deviceIoGate.WaitAsync(DeviceIoTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
            {
                AppLog.Info("Resetting device handles while an earlier operation is still stuck.");
            }
            try
            {
                if (!TrySetDevice(expected, null, message))
                {
                    return;
                }
                await transport.ResetConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (owned)
                {
                    deviceIoGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("Resetting cached device handles failed.", exception);
        }
    }

    private bool IsCurrentConnection(DeviceConnection expected)
    {
        lock (stateGate)
        {
            return IsCurrentConnectionLocked(expected);
        }
    }

    private bool IsCurrentConnectionLocked(DeviceConnection expected) =>
        deviceGeneration == expected.Generation &&
        ReferenceEquals(device, expected.Device);

    private async Task PrepareDeviceEpochAsync(
        PicoDeviceLocation target,
        CancellationToken cancellationToken)
    {
        await RunDeviceIoAsync(async token =>
        {
            var slot0 = await TryReadVibeKeyAsync(target.VibeSlot0Path, token)
                .ConfigureAwait(false);
            var slot1 = await TryReadVibeKeyAsync(target.VibeSlot1Path, token)
                .ConfigureAwait(false);
            var latest = SelectNewerKey(slot0, slot1);
            if (latest is null)
            {
                // Fresh firmware exposes two zero-filled 512-byte mailboxes.
                // They intentionally contain no key yet and are safe to claim
                // with this process's persisted epoch.
                return;
            }

            var nextSequence = PeekNextSequence();
            if (IsVibeKeyNewer(sessionEpoch, nextSequence, latest.Value.Epoch, latest.Value.Sequence))
            {
                return;
            }

            var previous = sessionEpoch;
            sessionEpoch = epochStore.EnsureNewerThan(sessionEpoch, latest.Value.Epoch);
            sequence = 0;
            vibeActive = false;
            lastRgbCommand = null;
            AppLog.Info(
                $"Advanced VIBE epoch from {previous} to {sessionEpoch} after observing " +
                $"device key {latest.Value.Epoch}/{latest.Value.Sequence}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VibeKey?> TryReadVibeKeyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var packet = new byte[VibePacketEncoder.SectorSize];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            VibePacketEncoder.SectorSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != packet.Length)
        {
            return null;
        }
        await stream.ReadExactlyAsync(packet, cancellationToken).ConfigureAwait(false);

        var validHeader = packet.AsSpan(0, 4).SequenceEqual("VIBE"u8) &&
            packet[4] == 1 &&
            packet[5] is (byte)VibePacketKind.Frame or (byte)VibePacketKind.Release &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2)) == VibePacketEncoder.HeaderSize;
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(VibePacketEncoder.CrcOffset, sizeof(uint)));
        if (!validHeader || VibePacketEncoder.ComputeCrc32(
                packet.AsSpan(0, VibePacketEncoder.CrcOffset)) != expectedCrc)
        {
            return null;
        }

        var epoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, sizeof(uint)));
        var packetSequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, sizeof(uint)));
        return epoch == 0 || packetSequence == 0
            ? null
            : new VibeKey(epoch, packetSequence);
    }

    private static VibeKey? SelectNewerKey(VibeKey? left, VibeKey? right)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        return IsVibeKeyNewer(right.Value.Epoch, right.Value.Sequence, left.Value.Epoch, left.Value.Sequence)
            ? right
            : left;
    }

    private static bool IsVibeKeyNewer(
        uint candidateEpoch,
        uint candidateSequence,
        uint currentEpoch,
        uint currentSequence) =>
        candidateEpoch != currentEpoch
            ? VibeSessionEpochStore.IsSerialNewer(candidateEpoch, currentEpoch)
            : VibeSessionEpochStore.IsSerialNewer(candidateSequence, currentSequence);

    private void SetDevice(PicoDeviceLocation? next, string message) =>
        _ = SetDevice(next, message, expected: null);

    private bool TrySetDevice(
        DeviceConnection expected,
        PicoDeviceLocation? next,
        string message) =>
        SetDevice(next, message, expected);

    private bool SetDevice(
        PicoDeviceLocation? next,
        string message,
        DeviceConnection? expected)
    {
        bool changed;
        PendingPermission? disconnectedPermission = null;
        lock (stateGate)
        {
            if (expected is { } required && !IsCurrentConnectionLocked(required))
            {
                return false;
            }

            changed = !ReferenceEquals(device, next);
            device = next;
            if (changed)
            {
                deviceGeneration = unchecked(deviceGeneration + 1);
                touchDeduplicator.Reset();
                vibeActive = false;
                lastRgbCommand = null;
            }
            if (next is null && pendingPermission is not null)
            {
                disconnectedPermission = pendingPermission;
                pendingPermission = null;
                lastRgbCommand = null;
            }
        }

        disconnectedPermission?.Completion.TrySetResult(HookDecision.None);

        if (changed || next is null)
        {
            AppLog.Info($"Device status: {message}.");
            DeviceStatusChanged?.Invoke(
                this,
                new DeviceStatus(next is not null, message, next?.RootPath));
        }

        return true;
    }

    private uint NextSequence()
    {
        var next = unchecked(++sequence);
        if (next == 0)
        {
            next = ++sequence;
        }
        return next;
    }

    private uint PeekNextSequence()
    {
        var next = unchecked(sequence + 1);
        return next == 0 ? 1u : next;
    }

    private static byte ToVibeBrightness(LightProfile profile, GlobalSettings global) =>
        (byte)(global.LightsEnabled
            ? RgbIniCommandBuilder.ApplyBrightnessCeiling(
                profile.Brightness,
                global.BrightnessPercent)
            : 0);

    private static LightProfile ProfileFor(AppConfig snapshot, AiState state) =>
        snapshot.Ai.StateProfiles.TryGetValue(state, out var id)
            ? ProfileFor(snapshot, id)
            : snapshot.Profiles.Values.First();

    private static LightProfile ProfileFor(AppConfig snapshot, string id) =>
        snapshot.Profiles.TryGetValue(id, out var profile)
            ? profile
            : snapshot.Profiles.Values.First();

    private static T NextEnum<T>(T current, T? skip, bool forward) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var index = Array.IndexOf(values, current);
        for (var attempt = 0; attempt < values.Length; attempt++)
        {
            index = (index + (forward ? 1 : -1) + values.Length) % values.Length;
            if (skip is null || !EqualityComparer<T>.Default.Equals(values[index], skip.Value))
            {
                return values[index];
            }
        }
        return current;
    }

    private static LightProfile CloneProfile(LightProfile source) => new()
    {
        Id = source.Id,
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

    internal static AppConfig CloneConfig(AppConfig source)
    {
        var clone = new AppConfig
        {
            SchemaVersion = source.SchemaVersion,
            Global = new GlobalSettings
            {
                BrightnessPercent = source.Global.BrightnessPercent,
                LedCount = source.Global.LedCount,
                LightsEnabled = source.Global.LightsEnabled,
                LaunchAtLogin = source.Global.LaunchAtLogin,
                CloseToTray = source.Global.CloseToTray,
                ActiveMode = source.Global.ActiveMode,
                AlwaysShowAiPermission = source.Global.AlwaysShowAiPermission,
                PreventSleepWhileLit = source.Global.PreventSleepWhileLit,
            },
            Ai = new AiSettings
            {
                DisplayMode = source.Ai.DisplayMode,
                MultiAgent = source.Ai.MultiAgent.Clone(),
                Enabled = source.Ai.Enabled,
                CursorShellGate = source.Ai.CursorShellGate,
                CursorMcpGate = source.Ai.CursorMcpGate,
                GateFileTools = source.Ai.GateFileTools,
                CodexTouchApproval = source.Ai.CodexTouchApproval,
                ApprovalTimeoutSeconds = source.Ai.ApprovalTimeoutSeconds,
                DoneOverlayMilliseconds = source.Ai.DoneOverlayMilliseconds,
                ErrorOverlayMilliseconds = source.Ai.ErrorOverlayMilliseconds,
                StateProfiles = new Dictionary<AiState, string>(source.Ai.StateProfiles),
            },
            SystemMonitor = new SystemMonitorSettings
            {
                Metric = source.SystemMonitor.Metric,
                SensorId = source.SystemMonitor.SensorId,
                Visualizer = source.SystemMonitor.Visualizer,
                UpdateIntervalMilliseconds = source.SystemMonitor.UpdateIntervalMilliseconds,
                DisplayMinimum = source.SystemMonitor.DisplayMinimum,
                DisplayMaximum = source.SystemMonitor.DisplayMaximum,
                ProfileId = source.SystemMonitor.ProfileId,
            },
            Music = new MusicSettings
            {
                EndpointId = source.Music.EndpointId,
                Visualizer = source.Music.Visualizer,
                ProfileId = source.Music.ProfileId,
                Sensitivity = source.Music.Sensitivity,
                NoiseGate = source.Music.NoiseGate,
                Attack = source.Music.Attack,
                Release = source.Music.Release,
                FramesPerSecond = source.Music.FramesPerSecond,
            },
            Touch = new TouchSettings
            {
                Enabled = source.Touch.Enabled,
                Contexts = source.Touch.Contexts.ToDictionary(
                    pair => pair.Key,
                    pair => new TouchBindings
                    {
                        Click = pair.Value.Click,
                        DoubleClick = pair.Value.DoubleClick,
                        LongPress = pair.Value.LongPress,
                    }),
            },
            Profiles = source.Profiles.ToDictionary(
                pair => pair.Key,
                pair => CloneProfile(pair.Value),
                StringComparer.OrdinalIgnoreCase),
        };
        return AppConfigDefaults.Normalize(clone);
    }

    private static async Task DelaySafeAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        shutdown.Cancel();
        PendingPermission? pending;
        lock (stateGate)
        {
            pending = pendingPermission;
            pendingPermission = null;
        }
        pending?.Completion.TrySetResult(HookDecision.None);

        var tasks = new[] { deviceTask, touchTask, lightingTask, metricTask, audioModeTask, eventRecoveryTask }
            .Where(task => task is not null)
            .Cast<Task>();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        var current = GetDevice();
        if (current is not null && vibeActive)
        {
            try
            {
                await transport.ReleaseVibeAsync(
                    current,
                    VibePacketEncoder.EncodeRelease(sessionEpoch, NextSequence()))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLog.Error("Releasing the VIBE lease during shutdown failed.", exception);
            }
        }

        if (pipeServer is not null)
        {
            await pipeServer.DisposeAsync().ConfigureAwait(false);
        }
        audioService.LevelChanged -= OnAudioLevelChanged;
        await audioService.DisposeAsync().ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
        permissionGate.Dispose();
        touchProcessingGate.Dispose();
        sleepGuard.Dispose();
        deviceIoGate.Dispose();
        deviceDiscoveryGate.Dispose();
        shutdown.Dispose();
        AppLog.Info("Resident broker stopped.");
    }

    private sealed record PendingPermission(
        HookRequest Request,
        TaskCompletionSource<HookDecision> Completion);

    private sealed record TouchDispatchPlan(
        TouchGesture Gesture,
        TouchAction Action,
        TouchContext Context,
        PendingPermission? Pending);

    private readonly record struct DeviceConnection(PicoDeviceLocation? Device, long Generation);

    private readonly record struct VibeKey(uint Epoch, uint Sequence);
}
