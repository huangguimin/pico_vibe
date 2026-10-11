namespace PicoVibe.Core.Lighting;

/// <summary>
/// Keeps music visualizers similarly responsive across players that expose
/// very different system-output levels. The same gain is applied to peak and
/// RMS so the musical crest factor is preserved.
/// </summary>
public sealed class MusicAutoGainController
{
    private const double TargetProgramLevel = 0.32;
    // ScreenCaptureKit is a digital source, but very small codec/dither values
    // still appear around silence. Keep them below AGC so they cannot turn into
    // a visible idle glow after a quiet player has learned a large gain.
    private const double MinimumProgramLevel = 0.006;
    private const double MaximumGain = 8.0;
    private const double ReferenceRiseSeconds = 0.18;
    private const double ReferenceFallSeconds = 4.0;
    private const double GainReductionSeconds = 0.12;
    private const double GainIncreaseSeconds = 1.1;
    private const double LowerSourceRatio = 0.40;
    private const double LowerSourceHoldSeconds = 1.25;
    private const double ResetAfterSilenceSeconds = 1.5;

    private DateTimeOffset? lastTimestamp;
    private double referenceLevel;
    private double currentGain = 1.0;
    private double lowerSourceSeconds;
    private double silenceSeconds;
    private bool initialized;

    /// <summary>The gain used for the most recently processed active sample.</summary>
    public double CurrentGain => currentGain;

    public AudioLevels Process(double peak, double rms, DateTimeOffset timestamp)
    {
        double elapsedSeconds = ElapsedSeconds(timestamp);
        peak = Sanitize(peak);
        rms = Sanitize(rms);

        var rawLevels = AudioLevels.FromPeakAndRms(peak, rms);
        double programLevel = rawLevels.Rms;
        if (programLevel <= MinimumProgramLevel)
        {
            silenceSeconds += elapsedSeconds;
            lowerSourceSeconds = 0;
            if (silenceSeconds >= ResetAfterSilenceSeconds)
            {
                ClearAdaptiveState();
            }
            return AudioLevels.FromPeakAndRms(0, 0);
        }

        silenceSeconds = 0;
        if (!initialized)
        {
            referenceLevel = programLevel;
            currentGain = DesiredGain(referenceLevel);
            initialized = true;
        }
        else
        {
            // A persistent large drop usually means that playback moved from
            // a browser to a normalized/Atmos system player. Rebase after a
            // short hold instead of taking many seconds to catch up. Brief
            // quiet passages continue to use the slow release below.
            if (programLevel < referenceLevel * LowerSourceRatio)
            {
                lowerSourceSeconds += elapsedSeconds;
            }
            else
            {
                lowerSourceSeconds = 0;
            }

            if (lowerSourceSeconds >= LowerSourceHoldSeconds)
            {
                referenceLevel = programLevel;
                lowerSourceSeconds = 0;
            }
            else
            {
                double referenceSeconds = programLevel > referenceLevel
                    ? ReferenceRiseSeconds
                    : ReferenceFallSeconds;
                referenceLevel = Smooth(
                    referenceLevel,
                    programLevel,
                    elapsedSeconds,
                    referenceSeconds);
            }

            double desiredGain = DesiredGain(referenceLevel);
            double gainSeconds = desiredGain < currentGain
                ? GainReductionSeconds
                : GainIncreaseSeconds;
            currentGain = Smooth(
                currentGain,
                desiredGain,
                elapsedSeconds,
                gainSeconds);
        }

        return AudioLevels.FromPeakAndRms(
            FrameColorMath.Clamp01(peak * currentGain),
            FrameColorMath.Clamp01(rms * currentGain));
    }

    public void Reset()
    {
        lastTimestamp = null;
        ClearAdaptiveState();
        silenceSeconds = 0;
    }

    private double ElapsedSeconds(DateTimeOffset timestamp)
    {
        double elapsed = lastTimestamp is { } previous
            ? (timestamp - previous).TotalSeconds
            : 0.04;
        lastTimestamp = timestamp;
        return double.IsFinite(elapsed) && elapsed > 0
            ? Math.Clamp(elapsed, 0.001, 0.5)
            : 0.04;
    }

    private void ClearAdaptiveState()
    {
        referenceLevel = 0;
        currentGain = 1.0;
        lowerSourceSeconds = 0;
        initialized = false;
    }

    private static double DesiredGain(double reference) =>
        Math.Clamp(
            TargetProgramLevel / Math.Max(reference, MinimumProgramLevel),
            1.0,
            MaximumGain);

    private static double Smooth(
        double current,
        double target,
        double elapsedSeconds,
        double timeConstantSeconds) =>
        current + ((target - current) *
            (1.0 - Math.Exp(-elapsedSeconds / timeConstantSeconds)));

    private static double Sanitize(double value) =>
        double.IsFinite(value) ? FrameColorMath.Clamp01(value) : 0.0;
}
