namespace SourdoughMonitor.Replay;

/// <summary>Per-frame result of a replay run: detection outcome, diagnostics, the measurement
/// itself, the analyzer's reading decision and the per-frame rise reading.</summary>
public sealed record ReplayFrameRow(
    string File,
    DateTimeOffset Time,
    string TimeSource,
    /// <summary>Detector outcome: "detected", "dark_frame", "decode_failed", "no_surface".</summary>
    string Outcome,
    string? Method,
    double? FrameMean,
    double? FrameMedian,
    double? FrameP10,
    double? FrameP90,
    double? BandContrast,
    int? BandTopRow,
    int? FinalRow,
    double? DoughTopPx,
    double? JarTopPx,
    double? JarBottomPx,
    int? JarLeftPx,
    int? JarRightPx,
    string? JarColumnKind,
    double? DoughHeightPx,
    /// <summary>"ok" when the analyzer produced a reading, "unavailable" when it gated the
    /// measurement (implausible jump / collapse confirmation), "" when no measurement existed.</summary>
    string Reading,
    double? RisePercent,
    double? RiseRatePctPerHour,
    double? PredictedPeakPercent,
    DateTimeOffset? PredictedPeakTime,
    bool Peaked,
    bool NewSession,
    /// <summary>Path of the annotated debug image written for this frame, relative to the replay output directory. Null when none was written.</summary>
    string? DebugImage);
