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

    public int PeakConfirmWindows { get; init; } = 3;

    public int MaxSessionHours { get; init; } = 36;

    /// <summary>Rolling median window (in samples) used to smooth raw dough-height
    /// readings before they enter the rise/slope/fit calculations.</summary>
    public int MedianWindowSize { get; init; } = 5;

    /// <summary>Rise-rate slope (%/h) at or below which the curve is considered flat.</summary>
    public double FlatSlopePercentPerHour { get; init; } = 0.5;

    /// <summary>Minimum observed rise (%) before a maximum forecast or a flat/falling curve
    /// may be declared a peak. Only blocks trivial drift: the protection against noise and
    /// exposure drift comes from <see cref="MinSlopeEvidenceSigmas"/> (a measured rise must
    /// have happened), so small but real rises (e.g. a kept starter) still get a forecast.</summary>
    public double MinRisePercentForPeak { get; init; } = 5;

    /// <summary>Minimum rise (%) before a forecast is published. A stall early in the rise (19 % for
    /// 45 minutes on 2026-10-14) looks like a turn and produced ETAs hours too early, so trivial
    /// rises never forecast. Keep it low: the evening re-feed of 2026-10-15 peaked at only 60 %, and
    /// a gate of 70 % (an earlier version) left that session without any forecast. A peak the rate
    /// itself confirms (<see cref="MinRisePercentForPeak"/>) is not affected.</summary>
    public double MinRisePercentForForecast { get; init; } = 30;

    /// <summary>Minutes after a session's start during which its baseline is the median of the
    /// readings so far instead of the first reading alone.</summary>
    public double BaselineSettleMinutes { get; init; } = 20;

    /// <summary>A peak or forecast requires the session's maximum rate to reach this many
    /// times <see cref="MaxRateStdErrPercentPerHour"/>: evidence that the dough really rose.</summary>
    public double MinSlopeEvidenceSigmas { get; init; } = 2.0;

    /// <summary>Physical limits of the dough level, as % of the session's starting height per
    /// hour. A fed dough rises at most about 26 %/h (measured) and falls after its peak at about
    /// 8 %/h; the limits leave headroom. They bound the trend the plausibility gate extrapolates and
    /// the movement it allows after a detection gap.</summary>
    public double MaxRisePercentPerHour { get; init; } = 40;

    public double MaxFallPercentPerHour { get; init; } = 15;

    /// <summary>Smallest deviation (px) from the expected level at which a raw reading is rejected
    /// outright as a misdetection (camera glitch, model error); three times the scatter of the
    /// recent readings if that is larger. At 9 px the gate dropped 10 genuine readings of the
    /// fast rise on 2026-10-15 (steps of 5-17 px); the misdetections it exists for sit 20 px off.</summary>
    public double OutlierTolerancePx { get; init; } = 12.0;

    /// <summary>Allowance (px) of the peak revocation: growth beyond the remembered peak by more
    /// than this is needed to un-peak.</summary>
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

    /// <summary>Consecutive minutes of <c>no_surface</c> after a confirmed peak before the
    /// loss is reported in the diagnostics: a peaked session whose surface stays invisible
    /// for hours usually means the detector lost the surface (collapse?), not that the
    /// dough vanished. Dark frames and other outcomes reset the streak — a dark night or a
    /// missing jar is not a lost surface.</summary>
    public int SurfaceLossAfterPeakMinutes { get; init; } = 120;

    public string? StateFilePath { get; init; } = "sourdough_state.json";
}