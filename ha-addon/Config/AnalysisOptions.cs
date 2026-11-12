namespace SourdoughMonitor.Config;

public sealed class AnalysisOptions
{
    /// <summary>Longest look-back (minutes) the rise rate may use. The rate is the slope of
    /// the shortest trailing span that already determines it well enough (see
    /// <see cref="MaxRateStdErrPercentPerHour"/>); a longer span lowers noise but biases the
    /// slope while growth accelerates or slows, so it is bounded.</summary>
    public int SlopeWindowMinutes { get; init; } = 120;

    /// <summary>Shortest trailing span (minutes) the rise rate is fitted over. 60 minutes follow the real
    /// rate through the whole rise (it reads 0 only when the dough has stopped) without the pixel-step
    /// spikes of 30-45 minute spans; capped by <see cref="SlopeWindowMinutes"/>.</summary>
    public int RateMinSpanMinutes { get; init; } = 60;

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

    /// <summary>The rise (%) the user acts on: "doubled" (100 %) is the rule for judging the dough
    /// ready (daily feed 30 g flour + 30 ml water + 3 g starter, bake build 80 + 80 + 8 g). The
    /// analyzer publishes when the rise is expected to reach it. 0 disables it.</summary>
    public double TargetRisePercent { get; init; } = 100;

    /// <summary>The rise (%) the "Target Rise ETA" counts down to while "Bread Mode" is on: a bread dough is
    /// baked at about +50 % (spelt gluten is weak, bake before the maximum), not at "doubled". 0 disables it.</summary>
    public double BreadTargetRisePercent { get; init; } = 50;

    /// <summary>Minutes before the forecast peak at which "Oven Preheat" turns on (the baker needs this long
    /// to heat the oven and the pot). It stays on once the peak is confirmed. 0 turns the signal off.</summary>
    public int OvenPreheatLeadMinutes { get; init; } = 30;

    /// <summary>File that remembers the "Bread Mode" switch across restarts; null = not persisted.</summary>
    public string? BreadModeFilePath { get; init; }

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

    /// <summary>The rise median of the last samples must exceed the new rise by at least this
    /// many percentage points for the relative drop to count as a collapse (see
    /// <see cref="ResetDropFraction"/>). Without it a wobble from +26 % to +17 % was a "collapse"
    /// and restarted the session (archive 2026-10: five sessions in eight hours on one jar);
    /// real feeding or punching down drops the rise by far more.</summary>
    public double CollapseMinDropPercentPoints { get; init; } = 25;

    /// <summary>Dough heights below this many pixels are detection failures (the archive has
    /// 2 to 11 px readings from a lost surface), not dough: such a measurement is unavailable
    /// and can neither start a session nor trigger a collapse. A session started on 11 px made
    /// the next one read +2064 %.</summary>
    public double MinPlausibleHeightPx { get; init; } = 30;

    /// <summary>The jar counts as removed when "empty_scene" frames (see
    /// <see cref="VisionOptions.EmptySceneMinMean"/>) span at least this many minutes without a
    /// measurement in between; the next measurement then starts a new session (a jar swap or a
    /// refilled jar is a new dough). Shorter gaps (a hand in front of the camera, a brightness
    /// flicker at the IR switch) do not.</summary>
    public double JarRemovedMinutes { get; init; } = 8;

    /// <summary>Empty-scene frames further apart than this many minutes belong to different
    /// absences (see <see cref="JarRemovedMinutes"/>). The archive's brightness flickers at the IR
    /// switch produced "absences" of 11 and 12 hours when scattered frames were chained.</summary>
    public double JarRemovedMaxGapMinutes { get; init; } = 15;

    /// <summary>A change of the height basis (dough floor, else glass bottom) of at least this many
    /// pixels between two measurements that the dough surface does not follow (it moved by at most
    /// 30 % of the shift) is a re-estimate of the jar, not growth: the session is rebased onto the new
    /// basis instead of counting the step as rise. Archive 2026-11-09: the basis went 656 to 694 px
    /// within two hours at a constant surface row, inflating the rise by 13 points.</summary>
    public double BasisShiftMinPx { get; init; } = 6;

    /// <summary>Longest gap between two measurements across which a basis shift is still attributed
    /// to a re-estimate (see <see cref="BasisShiftMinPx"/>); after a longer gap the surface may have
    /// moved unseen.</summary>
    public double BasisShiftMaxGapMinutes { get; init; } = 30;

    /// <summary>Usual height of the peak (rise in percent) of the daily starter, the centre of the
    /// log-normal prior of <see cref="PeakForecaster"/>. The 23 archive sessions of 2026-10 to 11 that
    /// ran 4 hours or more peaked between 40 and 181 %, mostly 67 to 155 %, median about 93 %. Jens
    /// expects the doubling for a starter.</summary>
    public double PeakPriorPercent { get; init; } = 100;

    /// <summary>The same for bread dough (Bread Mode), which is baked before the maximum: the
    /// 2026-10-29 sample peaked at +65 %.</summary>
    public double BreadPeakPriorPercent { get; init; } = 65;

    /// <summary>Spread of the peak prior in log space: 0.35 is a factor 1.4 up and down at one sigma.</summary>
    public double PeakPriorSigma { get; init; } = 0.35;

    /// <summary>Consecutive minutes of <c>no_surface</c> after a confirmed peak before the
    /// loss is reported in the diagnostics: a peaked session whose surface stays invisible
    /// for hours usually means the detector lost the surface (collapse?), not that the
    /// dough vanished. Dark frames and other outcomes reset the streak — a dark night or a
    /// missing jar is not a lost surface.</summary>
    public int SurfaceLossAfterPeakMinutes { get; init; } = 120;

    public string? StateFilePath { get; init; } = "sourdough_state.json";
}