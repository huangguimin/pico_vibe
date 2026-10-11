using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace PicoVibe.App.Services;

/// <summary>
/// Captures the current Windows default render endpoint with WASAPI loopback and
/// publishes smoothed peak/RMS values. It reopens the stream after endpoint
/// changes, unplug events, sleep/resume failures, and unexpected capture stops.
/// </summary>
public sealed class AudioLevelService : IDisposable, IAudioLevelService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan EndpointSettleDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan EndpointPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumEventInterval = TimeSpan.FromMilliseconds(40);
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _stateGate = new();
    private readonly object _eventGate = new();
    private readonly object _smoothingGate = new();
    private readonly SemaphoreSlim _lifecycleMutex = new(1, 1);
    private readonly SemaphoreSlim _restartSignal = new(0, 1);

    private EventHandler<AudioLevelSample>? _levelChanged;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _supervisorTask;
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _captureDevice;
    private string? _captureDeviceId;
    private string _activeSource = "WASAPI loopback";
    private string _restartReason = "Audio endpoint changed; reconnecting.";
    private AudioLevelSample _current = AudioLevelSample.Unavailable("Audio capture has not started.");
    private double _smoothedPeak;
    private double _smoothedRms;
    private long _lastSmoothingTimestamp;
    private long _lastEventTimestamp;
    private long _attackTicks = TimeSpan.FromMilliseconds(30).Ticks;
    private long _releaseTicks = TimeSpan.FromMilliseconds(250).Ticks;
    private int _disposeState;

    /// <summary>
    /// Raised on a capture/background thread, at no more than 25 Hz for normal
    /// samples. WPF subscribers must marshal UI work through Dispatcher.
    /// Subscriber exceptions are isolated and cannot stop audio capture.
    /// </summary>
    public event EventHandler<AudioLevelSample> LevelChanged
    {
        add
        {
            lock (_eventGate)
            {
                _levelChanged += value;
            }
        }
        remove
        {
            lock (_eventGate)
            {
                _levelChanged -= value;
            }
        }
    }

    public AudioLevelSample Current => Volatile.Read(ref _current);

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _supervisorTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>Time constant used while the input level is rising.</summary>
    public TimeSpan AttackTime
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _attackTicks));
        set => Interlocked.Exchange(ref _attackTicks, ValidateTimeConstant(value, nameof(AttackTime)).Ticks);
    }

    /// <summary>Time constant used while the input level is falling.</summary>
    public TimeSpan ReleaseTime
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _releaseTicks));
        set => Interlocked.Exchange(ref _releaseTicks, ValidateTimeConstant(value, nameof(ReleaseTime)).Ticks);
    }

    /// <summary>Starts the reconnecting capture supervisor. Calling it twice is safe.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await _lifecycleMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_stateGate)
            {
                if (_supervisorTask is { IsCompleted: false })
                {
                    return;
                }

                _lifetimeCts?.Dispose();
                _lifetimeCts = new CancellationTokenSource();
                CancellationToken lifetimeToken = _lifetimeCts.Token;
                _supervisorTask = Task.Run(
                    () => RunSupervisorAsync(lifetimeToken),
                    CancellationToken.None);
            }
        }
        finally
        {
            _lifecycleMutex.Release();
        }
    }

    /// <summary>Stops capture and waits for all COM/capture resources to be released.</summary>
    public async Task StopAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        await _lifecycleMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? supervisor;
            CancellationTokenSource? lifetimeCts;
            lock (_stateGate)
            {
                supervisor = _supervisorTask;
                lifetimeCts = _lifetimeCts;
            }

            if (supervisor is null)
            {
                return;
            }

            lifetimeCts?.Cancel();
            SignalRestart("Audio monitoring is stopping.");

            try
            {
                await supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The supervisor uses cancellation as its normal stop path.
            }

            lock (_stateGate)
            {
                if (ReferenceEquals(_supervisorTask, supervisor))
                {
                    _supervisorTask = null;
                    _lifetimeCts = null;
                }
            }

            lifetimeCts?.Dispose();
            ResetSmoothing();
            Publish(AudioLevelSample.Unavailable("Audio monitoring is stopped."), forceEvent: true);
        }
        finally
        {
            _lifecycleMutex.Release();
        }
    }

    private async Task RunSupervisorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MMDeviceEnumerator? enumerator = null;
            EndpointNotificationClient? notifications = null;
            bool notificationsRegistered = false;
            bool captureStarted = false;
            string reconnectReason = "WASAPI loopback stopped; reconnecting.";

            try
            {
                DrainRestartSignal();
                enumerator = new MMDeviceEnumerator();
                notifications = new EndpointNotificationClient(this);

                try
                {
                    int result = enumerator.RegisterEndpointNotificationCallback(notifications);
                    notificationsRegistered = result >= 0;
                }
                catch
                {
                    // The periodic endpoint-ID check below is a complete fallback.
                }

                StartCapture(enumerator);
                captureStarted = true;
                await WaitForRestartOrEndpointChangeAsync(enumerator, cancellationToken)
                    .ConfigureAwait(false);
                reconnectReason = ConsumeRestartReason();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                reconnectReason =
                    $"WASAPI loopback is unavailable: {GetSafeExceptionMessage(exception)}";
            }
            finally
            {
                StopCurrentCapture();

                if (enumerator is not null && notifications is not null && notificationsRegistered)
                {
                    try
                    {
                        _ = enumerator.UnregisterEndpointNotificationCallback(notifications);
                    }
                    catch
                    {
                    }
                }

                enumerator?.Dispose();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            ResetSmoothing();
            Publish(AudioLevelSample.Unavailable(reconnectReason), forceEvent: true);

            TimeSpan delay = captureStarted ? EndpointSettleDelay : RetryDelay;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        StopCurrentCapture();
    }

    private void StartCapture(MMDeviceEnumerator enumerator)
    {
        MMDevice? device = null;
        WasapiLoopbackCapture? capture = null;
        bool installedAsCurrentCapture = false;

        try
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string deviceId = device.ID;
            string source = string.IsNullOrWhiteSpace(device.FriendlyName)
                ? "WASAPI loopback (default render endpoint)"
                : $"WASAPI loopback ({LimitText(device.FriendlyName, 100)})";

            capture = new WasapiLoopbackCapture(device);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            lock (_stateGate)
            {
                _captureDevice = device;
                _capture = capture;
                _captureDeviceId = deviceId;
                _activeSource = source;
            }
            installedAsCurrentCapture = true;

            ResetSmoothing();
            capture.StartRecording();
            Publish(AudioLevelSample.Success(0, 0, source), forceEvent: true);

            // Ownership has transferred to the service fields.
            device = null;
            capture = null;
        }
        catch
        {
            if (installedAsCurrentCapture)
            {
                StopCurrentCapture();
            }
            else if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                device?.Dispose();
            }
            else
            {
                device?.Dispose();
            }

            throw;
        }
    }

    private async Task WaitForRestartOrEndpointChangeAsync(
        MMDeviceEnumerator enumerator,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool signaled = await _restartSignal
                .WaitAsync(EndpointPollInterval, cancellationToken)
                .ConfigureAwait(false);
            if (signaled)
            {
                return;
            }

            using MMDevice currentDefault =
                enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string? capturedDeviceId;
            lock (_stateGate)
            {
                capturedDeviceId = _captureDeviceId;
            }

            if (!string.Equals(currentDefault.ID, capturedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                SignalRestart("The default audio output changed; reconnecting.");
                return;
            }
        }
    }

    private void StopCurrentCapture()
    {
        WasapiLoopbackCapture? capture;
        MMDevice? device;
        lock (_stateGate)
        {
            capture = _capture;
            device = _captureDevice;
            _capture = null;
            _captureDevice = null;
            _captureDeviceId = null;
            _activeSource = "WASAPI loopback";
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                capture.StopRecording();
            }
            catch
            {
            }

            try
            {
                capture.Dispose();
            }
            catch
            {
            }
        }

        try
        {
            device?.Dispose();
        }
        catch
        {
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposeState) != 0 || sender is not WasapiLoopbackCapture capture)
        {
            return;
        }

        lock (_stateGate)
        {
            if (!ReferenceEquals(_capture, capture))
            {
                return;
            }
        }

        try
        {
            if (!TryCalculateLevels(
                    eventArgs.Buffer,
                    eventArgs.BytesRecorded,
                    capture.WaveFormat,
                    out double rawPeak,
                    out double rawRms,
                    out string reason))
            {
                SignalRestart(reason);
                return;
            }

            (double peak, double rms) = Smooth(rawPeak, rawRms);
            string source;
            lock (_stateGate)
            {
                source = _activeSource;
            }

            Publish(AudioLevelSample.Success(peak, rms, source), forceEvent: false);
        }
        catch (Exception exception)
        {
            SignalRestart($"Audio sample decoding failed: {GetSafeExceptionMessage(exception)}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_capture, sender))
            {
                return;
            }
        }

        string reason = eventArgs.Exception is null
            ? "WASAPI loopback stopped unexpectedly; reconnecting."
            : $"WASAPI loopback stopped: {GetSafeExceptionMessage(eventArgs.Exception)}";
        SignalRestart(reason);
    }

    private static bool TryCalculateLevels(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat inputFormat,
        out double peak,
        out double rms,
        out string reason)
    {
        peak = 0;
        rms = 0;
        reason = string.Empty;

        if (bytesRecorded <= 0)
        {
            return true;
        }

        int availableBytes = Math.Min(bytesRecorded, buffer.Length);
        WaveFormat format = inputFormat;
        if (inputFormat is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat != FloatSubFormat && extensible.SubFormat != PcmSubFormat)
            {
                reason = $"Unsupported WASAPI sub-format: {extensible.SubFormat}.";
                return false;
            }

            format = extensible.ToStandardWaveFormat();
        }

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        int bits = format.BitsPerSample;
        int channels = format.Channels;
        int bytesPerSample = bits / 8;
        int blockAlign = format.BlockAlign;

        bool supported = (isFloat && bits is 32 or 64) ||
                         (isPcm && bits is 8 or 16 or 24 or 32);
        if (!supported || channels <= 0 || bytesPerSample <= 0 || blockAlign < channels * bytesPerSample)
        {
            reason = $"Unsupported WASAPI sample format: {format}.";
            return false;
        }

        long sampleCount = 0;
        double sumSquares = 0;
        double maximum = 0;

        for (int frameOffset = 0;
             frameOffset + blockAlign <= availableBytes;
             frameOffset += blockAlign)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = frameOffset + (channel * bytesPerSample);
                double sample = isFloat
                    ? ReadFloatSample(buffer, offset, bits)
                    : ReadPcmSample(buffer, offset, bits);

                if (!double.IsFinite(sample))
                {
                    continue;
                }

                sample = Math.Clamp(sample, -1.0, 1.0);
                double absolute = Math.Abs(sample);
                maximum = Math.Max(maximum, absolute);
                sumSquares += sample * sample;
                sampleCount++;
            }
        }

        if (sampleCount == 0)
        {
            reason = "WASAPI returned an empty or incomplete audio buffer.";
            return false;
        }

        peak = Math.Clamp(maximum, 0.0, 1.0);
        rms = Math.Clamp(Math.Sqrt(sumSquares / sampleCount), 0.0, 1.0);
        return true;
    }

    private static double ReadFloatSample(byte[] buffer, int offset, int bits) => bits switch
    {
        32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4))),
        64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8))),
        _ => 0,
    };

    private static double ReadPcmSample(byte[] buffer, int offset, int bits) => bits switch
    {
        8 => (buffer[offset] - 128) / 128.0,
        16 => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)) / 32768.0,
        24 => ReadPcm24(buffer, offset) / 8388608.0,
        32 => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4)) / 2147483648.0,
        _ => 0,
    };

    private static int ReadPcm24(byte[] buffer, int offset)
    {
        int value = buffer[offset] |
                    (buffer[offset + 1] << 8) |
                    (buffer[offset + 2] << 16);
        return (value & 0x00800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    private (double Peak, double Rms) Smooth(double rawPeak, double rawRms)
    {
        lock (_smoothingGate)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedSeconds = _lastSmoothingTimestamp == 0
                ? 0.02
                : Math.Clamp(Stopwatch.GetElapsedTime(_lastSmoothingTimestamp, now).TotalSeconds, 0.001, 1.0);

            _lastSmoothingTimestamp = now;
            _smoothedPeak = ApplyEnvelope(_smoothedPeak, rawPeak, elapsedSeconds);
            _smoothedRms = ApplyEnvelope(_smoothedRms, rawRms, elapsedSeconds);
            return (_smoothedPeak, _smoothedRms);
        }
    }

    private double ApplyEnvelope(double current, double target, double elapsedSeconds)
    {
        long timeConstantTicks = target > current
            ? Interlocked.Read(ref _attackTicks)
            : Interlocked.Read(ref _releaseTicks);
        double timeConstantSeconds = TimeSpan.FromTicks(timeConstantTicks).TotalSeconds;
        double alpha = 1.0 - Math.Exp(-elapsedSeconds / timeConstantSeconds);
        return Math.Clamp(current + ((target - current) * alpha), 0.0, 1.0);
    }

    private void ResetSmoothing()
    {
        lock (_smoothingGate)
        {
            _smoothedPeak = 0;
            _smoothedRms = 0;
            _lastSmoothingTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void Publish(AudioLevelSample sample, bool forceEvent)
    {
        Volatile.Write(ref _current, sample);

        if (!forceEvent)
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Volatile.Read(ref _lastEventTimestamp);
            if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < MinimumEventInterval)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastEventTimestamp, now, previous) != previous)
            {
                return;
            }
        }
        else
        {
            Interlocked.Exchange(ref _lastEventTimestamp, Stopwatch.GetTimestamp());
        }

        EventHandler<AudioLevelSample>? handlers;
        lock (_eventGate)
        {
            handlers = _levelChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<AudioLevelSample> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, sample);
            }
            catch
            {
                // A UI/plugin subscriber cannot be allowed to kill the capture thread.
            }
        }
    }

    private void SignalRestart(string reason)
    {
        lock (_stateGate)
        {
            _restartReason = reason;
        }

        try
        {
            if (_restartSignal.CurrentCount == 0)
            {
                _restartSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string ConsumeRestartReason()
    {
        lock (_stateGate)
        {
            string reason = _restartReason;
            _restartReason = "Audio endpoint changed; reconnecting.";
            return reason;
        }
    }

    private void DrainRestartSignal()
    {
        try
        {
            while (_restartSignal.Wait(0))
            {
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool IsCurrentDevice(string deviceId)
    {
        lock (_stateGate)
        {
            return string.Equals(_captureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static TimeSpan ValidateTimeConstant(TimeSpan value, string propertyName)
    {
        if (value < TimeSpan.FromMilliseconds(1) || value > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                value,
                "The smoothing time constant must be between 1 ms and 10 seconds.");
        }

        return value;
    }

    private static string GetSafeExceptionMessage(Exception exception) =>
        LimitText(string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message, 180);

    private static string LimitText(string value, int maximumLength)
    {
        string normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumLength - 1), "…");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        StopCoreAsync().GetAwaiter().GetResult();
        _restartSignal.Dispose();
        _lifecycleMutex.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await StopCoreAsync().ConfigureAwait(false);
        _restartSignal.Dispose();
        _lifecycleMutex.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class EndpointNotificationClient(AudioLevelService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (owner.IsCurrentDevice(deviceId) && (newState & DeviceState.Active) == 0)
            {
                owner.SignalRestart("The active audio output became unavailable; reconnecting.");
            }
        }

        public void OnDeviceAdded(string deviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (owner.IsCurrentDevice(deviceId))
            {
                owner.SignalRestart("The active audio output was removed; reconnecting.");
            }
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role is Role.Multimedia or Role.Console)
            {
                owner.SignalRestart("The default audio output changed; reconnecting.");
            }
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
        }
    }
}
