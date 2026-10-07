namespace SourdoughMonitor.Config;

public sealed class AnalysisOptions
{
    /// <summary>Longest look-back (minutes) the rise rate may use. The rate is the slope of
    /// the shortest trailing span that already determines it well enough (see
    /// <see cref="MaxRateStdErrPercentPerHour"/>); a longer span lowers noise but biases the
    /// slope while growth accelerates or slows, so it is bounded.</summary>
    public int SlopeWindowMinutes { get; init; } = 120;

    /// <summary>Standard deviation (px) of a single dough-height reading around the true
    /// surface. Measured about 0.8 px on a static jar; the effective value is about twice as
    /// large because the noise is correlated over minutes, so 2.0 px keeps the computed
    /// slope error honest. It is also the lower bound on the noise estimated from the fit,
    /// so a handful of lucky samples cannot claim a precise rate.</summary>
    public double RateNoiseFloorPx { get; init; } = 2.0;

    /// <summary>The rise rate is published only once its standard error (%/h) is at most this
    /// value. A rate right after a reset, from a few jittery frames, or across a step in
    /// the data has a large error and is withheld instead of being published or clipped.</summary>
    public double MaxRateStdErrPercentPerHour { get; init; } = 2.0;

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

    /// <summary>Minimum observed rise (%) before a maximum forecast or a flat/falling curve
    /// may be declared a peak. Only blocks trivial drift: the protection against noise and
    /// exposure drift comes from <see cref="MinSlopeEvidenceSigmas"/> (a measured rise must
    /// have happened), so small but real rises (e.g. a kept starter) still get a forecast.</summary>
    public double MinRisePercentForPeak { get; init; } = 5;

    /// <summary>A peak or forecast requires the session's maximum rate to reach this many
    /// times <see cref="MaxRateStdErrPercentPerHour"/> (evidence of a real rise), and the
    /// current rate to sit measurably below that maximum (a measured turn — the margin
    /// grows with steep sessions, see the analyzer).</summary>
    public double MinSlopeEvidenceSigmas { get; init; } = 2.0;

    /// <summary>Window (minutes) of recent peak-ETA estimates that must agree before a point
    /// ETA is published.</summary>
    public int EtaStabilityWindowMinutes { get; init; } = 30;

    /// <summary>Largest spread (minutes) among the ETA estimates in the stability window for
    /// which the point ETA is still published.</summary>
    public double MaxEtaSpreadMinutes { get; init; } = 30;

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