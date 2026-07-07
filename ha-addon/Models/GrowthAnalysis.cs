namespace SourdoughMonitor.Analysis;

public sealed record GrowthAnalysis(
    double CurrentHeightPx,
    double StartHeightPx,
    double GrowthFactor,
    double RiseRatePxPerHour,
    double AccelerationPxPerHour2,
    RiseTrend Trend,
    DateTimeOffset? PredictedPeakTime,
    double? PredictedPeakHeightPx,
    DateTimeOffset? EstimatedTargetTime,
    GrowthPhase Phase);