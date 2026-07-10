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

    public string? StateFilePath { get; init; } = "sourdough_state.json";
}