namespace SourdoughMonitor.Config;

/// <summary>Tuning knobs for GrowthTracker. Defaults assume a capture interval
/// of roughly 1-5 minutes and a typical home-fermentation rise of several hours.</summary>
public sealed class GrowthOptions
{
    /// <summary>Hard physical limit for accepted movement between consecutive frames.</summary>
    public double MaxRisePxPerMinute { get; init; } = 4.0;

    /// <summary>Detector jitter allowance added on top of the physical limit,
    /// and used as the noise floor in phase classification.</summary>
    public double JitterTolerancePx { get; init; } = 6.0;

    /// <summary>Rolling median window over raw heights (odd values behave best).</summary>
    public int MedianWindowSize { get; init; } = 5;

    /// <summary>Trailing window for the rise-rate estimate.</summary>
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Below this rate (px/h) the curve counts as flat.</summary>
    public double FlatRatePxPerHour { get; init; } = 5.0;

    /// <summary>Minimum accepted samples before fitting/classification starts.</summary>
    public int MinSamplesForFit { get; init; } = 10;

    /// <summary>Fraction of the fitted plateau treated as the practical peak.</summary>
    public double PeakFraction { get; init; } = 0.97;

    /// <summary>Growth factor treated as ready (for example 1.75x for bulk fermentation).</summary>
    public double TargetGrowthFactor { get; init; } = 1.75;

    /// <summary>Linear ETAs further out than this are suppressed as not credible.</summary>
    public TimeSpan MaxEtaHorizon { get; init; } = TimeSpan.FromHours(16);

    /// <summary>How much history to keep in memory.</summary>
    public TimeSpan HistoryRetention { get; init; } = TimeSpan.FromHours(24);
}
