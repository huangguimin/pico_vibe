using PicoVibe.Core.Configuration;

namespace PicoVibe.Core.Touch;

/// <summary>One firmware-decoded TOUCH.TXT gesture.</summary>
public readonly record struct TouchEvent(
    ulong Sequence,
    TouchGesture Gesture,
    ulong DeviceMilliseconds);

public static class TouchEventParser
{
    public static bool TryParse(string? text, out TouchEvent touchEvent)
    {
        touchEvent = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (!fields.TryGetValue("seq", out var sequenceText) ||
            !ulong.TryParse(sequenceText, out var sequence) ||
            !fields.TryGetValue("evt", out var gestureText) ||
            !TryParseGesture(gestureText, out var gesture) ||
            !fields.TryGetValue("t_ms", out var millisecondsText) ||
            !ulong.TryParse(millisecondsText, out var milliseconds))
        {
            return false;
        }

        touchEvent = new TouchEvent(sequence, gesture, milliseconds);
        return true;
    }

    private static bool TryParseGesture(string value, out TouchGesture gesture)
    {
        gesture = value.Trim().ToLowerInvariant() switch
        {
            "none" => TouchGesture.None,
            "click" => TouchGesture.Click,
            "dblclick" => TouchGesture.DoubleClick,
            "long" => TouchGesture.LongPress,
            _ => (TouchGesture)(-1),
        };
        return Enum.IsDefined(gesture);
    }
}
