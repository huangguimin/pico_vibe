using PicoVibe.Core.Configuration;
using PicoVibe.Core.Touch;

namespace PicoVibe.Tests;

public sealed class TouchTests
{
    [Theory]
    [InlineData("click", TouchGesture.Click)]
    [InlineData("dblclick", TouchGesture.DoubleClick)]
    [InlineData("long", TouchGesture.LongPress)]
    [InlineData("none", TouchGesture.None)]
    public void Parser_ParsesFirmwareFormat(string wireGesture, TouchGesture expected)
    {
        var text = $"seq=42\nevt={wireGesture}\nt_ms=12345\n";

        Assert.True(TouchEventParser.TryParse(text, out var touch));
        Assert.Equal((ulong)42, touch.Sequence);
        Assert.Equal(expected, touch.Gesture);
        Assert.Equal((ulong)12345, touch.DeviceMilliseconds);
    }

    [Fact]
    public void Parser_RejectsPartialOrUnknownPayload()
    {
        Assert.False(TouchEventParser.TryParse("seq=1\nevt=swipe\nt_ms=5", out _));
        Assert.False(TouchEventParser.TryParse("seq=1\nevt=click", out _));
    }

    [Fact]
    public void Deduplicator_IsExactlyOnceAndStartsNewEpochOnRollback()
    {
        var dedupe = new TouchEventDeduplicator();

        Assert.Equal(
            TouchObservationKind.Baseline,
            dedupe.Observe("H:\\", new TouchEvent(5, TouchGesture.Click, 500)).Kind);
        Assert.Equal(
            TouchObservationKind.Duplicate,
            dedupe.Observe("H:\\", new TouchEvent(5, TouchGesture.Click, 500)).Kind);
        var next = dedupe.Observe("H:\\", new TouchEvent(6, TouchGesture.DoubleClick, 600));
        Assert.Equal(TouchObservationKind.NewEvent, next.Kind);
        Assert.True(next.ShouldDispatch);

        var reset = dedupe.Observe("H:\\", new TouchEvent(0, TouchGesture.None, 0));
        Assert.Equal(TouchObservationKind.NewEpoch, reset.Kind);
        Assert.False(reset.ShouldDispatch);
        Assert.Equal(2, reset.Epoch);
        Assert.Equal(
            TouchObservationKind.NewEvent,
            dedupe.Observe("H:\\", new TouchEvent(1, TouchGesture.LongPress, 900)).Kind);
    }

    [Fact]
    public void Deduplicator_DeviceChangeIsBaselineForNewEpoch()
    {
        var dedupe = new TouchEventDeduplicator();
        dedupe.Observe("H:\\", new TouchEvent(10, TouchGesture.None, 1000));

        var changed = dedupe.Observe("I:\\", new TouchEvent(50, TouchGesture.Click, 9000));

        Assert.Equal(TouchObservationKind.NewEpoch, changed.Kind);
        Assert.False(changed.ShouldDispatch);
    }

    [Fact]
    public void Deduplicator_DoesNotLoseEventWhenDeviceMillisecondCounterWraps()
    {
        var dedupe = new TouchEventDeduplicator();
        dedupe.Observe(
            "H:\\",
            new TouchEvent(40, TouchGesture.Click, uint.MaxValue - 10));

        var wrapped = dedupe.Observe(
            "H:\\",
            new TouchEvent(41, TouchGesture.DoubleClick, 25));

        Assert.Equal(TouchObservationKind.NewEvent, wrapped.Kind);
        Assert.True(wrapped.ShouldDispatch);
    }

    [Fact]
    public void Deduplicator_SameSequenceWithClockRollbackStartsNewEpoch()
    {
        var dedupe = new TouchEventDeduplicator();
        dedupe.Observe("H:\\", new TouchEvent(8, TouchGesture.Click, 1000));

        var reset = dedupe.Observe("H:\\", new TouchEvent(8, TouchGesture.Click, 10));

        Assert.Equal(TouchObservationKind.NewEpoch, reset.Kind);
        Assert.False(reset.ShouldDispatch);
    }
}
