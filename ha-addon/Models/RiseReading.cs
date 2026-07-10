namespace SourdoughMonitor.Analysis;

public sealed record RiseReading(
    DateTimeOffset Time,
    double RisePercent,
    double? RiseRatePercentPerHour,
    double? PredictedPeakPercent,
    DateTimeOffset? PredictedPeakTime,
    bool Peaked,
    bool NewSession,
    DateTimeOffset? SessionStart = null);