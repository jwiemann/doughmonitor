namespace SourdoughMonitor.Replay;

/// <summary>Per-frame result of a replay run: detection outcome, diagnostics, the measurement
/// itself, the tracker gate decision and the per-frame rise reading.</summary>
public sealed record ReplayFrameRow(
    string File,
    DateTimeOffset Time,
    string TimeSource,
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
    double? DoughHeightPx,
    double? SmoothedHeightPx,
    string Gate,
    double? RisePercent,
    double? RiseRatePctPerHour,
    double? PredictedPeakPercent,
    DateTimeOffset? PredictedPeakTime,
    bool Peaked,
    bool NewSession,
    /// <summary>Path of the annotated debug image written for this frame, relative to the replay output directory. Null when none was written.</summary>
    string? DebugImage);
