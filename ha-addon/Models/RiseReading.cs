namespace SourdoughMonitor.Analysis;

public sealed record RiseReading(
    DateTimeOffset Time,
    double RisePercent,
    double? RiseRatePercentPerHour,
    double? PredictedPeakPercent,
    DateTimeOffset? PredictedPeakTime,
    DateTimeOffset? PredictedPeakTimeLow,
    DateTimeOffset? PredictedPeakTimeHigh,
    bool Peaked,
    bool NewSession,
    DateTimeOffset? SessionStart = null);