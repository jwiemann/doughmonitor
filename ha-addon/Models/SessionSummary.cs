namespace SourdoughMonitor.Analysis;

public sealed record SessionSummary(
    DateTimeOffset SessionStart,
    DateTimeOffset SessionEnd,
    double MaxRisePercent,
    DateTimeOffset MaxRiseTime,
    double? MaxRatePercentPerHour,
    DateTimeOffset? PredictedPeakTime,
    double? PredictionErrorMinutes);
