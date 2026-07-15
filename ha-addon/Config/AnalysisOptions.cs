namespace SourdoughMonitor.Config;

public sealed class AnalysisOptions
{
    public int SlopeWindowMinutes { get; init; } = 40;

    public double ResetDropFraction { get; init; } = 0.25;

    public int MinSamplesForFit { get; init; } = 8;

    public double MaxEtaRelativeStdError { get; init; } = 0.15;

    public int PeakConfirmWindows { get; init; } = 3;

    public int MaxSessionHours { get; init; } = 36;

    /// <summary>Rolling median window (in samples) used to smooth raw dough-height
    /// readings before they enter the rise/slope/fit calculations.</summary>
    public int MedianWindowSize { get; init; } = 5;

    /// <summary>Fraction of the fitted sigmoid's asymptote treated as "practically peaked"
    /// (the true asymptote is never reached, and dough collapses before it). Drives both
    /// the reported predicted-peak rise and the peak ETA.</summary>
    public double PeakFraction { get; init; } = 0.97;

    /// <summary>Rise-rate slope (%/h) at or below which the curve is considered flat.</summary>
    public double FlatSlopePercentPerHour { get; init; } = 0.5;

    /// <summary>Minimum observed rise (%) required before a flat/falling slope can be
    /// interpreted as "peaked" rather than "still in the lag phase". Only used as a
    /// fallback when no sigmoid fit is available yet.</summary>
    public double MinRisePercentForPeak { get; init; } = 25;

    /// <summary>Physical plausibility gate: dough cannot rise or fall faster than this many
    /// pixels per minute. A raw reading that implies a faster change than
    /// (MaxRisePxPerMinute * elapsed minutes + JitterTolerancePx) is rejected outright
    /// (camera glitch, misdetected frame) rather than smoothed in and reported.</summary>
    public double MaxRisePxPerMinute { get; init; } = 4.0;

    /// <summary>Flat allowance added on top of the rate-based plausibility budget, so normal
    /// per-frame jitter between consecutive samples isn't rejected.</summary>
    public double JitterTolerancePx { get; init; } = 6.0;

    /// <summary>How many consecutive frames the plausibility gate above may reject before
    /// giving up and accepting the reading anyway. Real dough handling - feeding the
    /// starter, punching down, folding - moves the surface faster than the gate's budget
    /// allows but keeps showing the new height on the next sample, unlike a one-off
    /// misdetected frame that reverts. Bounds how long a legitimate handling event is
    /// reported as unavailable.</summary>
    public int MaxImplausibleJumpRejects { get; init; } = 2;

    /// <summary>How many consecutive samples must all look like a collapse (see
    /// <see cref="ResetDropFraction"/>) before the session is actually reset. A jar
    /// reappearing after a detection gap (occlusion, glare while the vision pipeline
    /// reacquires the surface) often produces exactly one frame that looks like a big drop;
    /// a real collapse (punch-down, deflating starter) keeps reporting the lower level
    /// instead of reverting on the next sample.</summary>
    public int CollapseConfirmSamples { get; init; } = 3;

    public string? StateFilePath { get; init; } = "sourdough_state.json";
}