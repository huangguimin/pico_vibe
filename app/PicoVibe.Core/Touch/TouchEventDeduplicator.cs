namespace PicoVibe.Core.Touch;

public enum TouchObservationKind
{
    Baseline,
    Duplicate,
    NewEvent,
    NewEpoch,
}

public readonly record struct TouchObservation(
    TouchObservationKind Kind,
    TouchEvent Event,
    long Epoch)
{
    public bool ShouldDispatch => Kind == TouchObservationKind.NewEvent;
}

/// <summary>
/// Converts the device's seq/t_ms stream into exactly-once observations. A
/// device change or counter rollback starts a new epoch and is treated as a
/// baseline, preventing a stale event from firing on reconnect.
/// </summary>
public sealed class TouchEventDeduplicator
{
    private string? deviceId;
    private TouchEvent previous;
    private bool hasBaseline;

    public long Epoch { get; private set; }
    public ulong? CurrentSequence => hasBaseline ? previous.Sequence : null;

    public TouchObservation Observe(string deviceId, TouchEvent touchEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!hasBaseline)
        {
            this.deviceId = deviceId;
            previous = touchEvent;
            hasBaseline = true;
            Epoch = 1;
            return new TouchObservation(TouchObservationKind.Baseline, touchEvent, Epoch);
        }

        if (!string.Equals(this.deviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
            touchEvent.Sequence < previous.Sequence ||
            (touchEvent.Sequence == previous.Sequence &&
             touchEvent.DeviceMilliseconds < previous.DeviceMilliseconds))
        {
            this.deviceId = deviceId;
            previous = touchEvent;
            Epoch++;
            return new TouchObservation(TouchObservationKind.NewEpoch, touchEvent, Epoch);
        }

        if (touchEvent.Sequence <= previous.Sequence)
        {
            return new TouchObservation(TouchObservationKind.Duplicate, touchEvent, Epoch);
        }

        previous = touchEvent;
        return new TouchObservation(TouchObservationKind.NewEvent, touchEvent, Epoch);
    }

    public void Reset()
    {
        deviceId = null;
        previous = default;
        hasBaseline = false;
        Epoch = 0;
    }
}
