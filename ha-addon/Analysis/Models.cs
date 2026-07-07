namespace SourdoughMonitor.Analysis;

public sealed record LevelMeasurement(DateTimeOffset Time, double DoughTopPx, double JarTopPx, double JarBottomPx)
{
    public double DoughHeightPx => JarBottomPx - DoughTopPx;
}

public sealed record Sample(DateTimeOffset Time, double RisePercent);

public sealed record SigmoidFit(double L, double K, double T0, double RelativeStdError)
{
    /// <summary>Time at which the curve reaches ~88% of L, used as practical peak ETA.</summary>
    public double PeakHoursFromStart => T0 + 2.0 / K;
}

public sealed record RiseReading(
    DateTimeOffset Time,
    double RisePercent,
    double? RiseRatePercentPerHour,
    double? PredictedPeakPercent,
    DateTimeOffset? PredictedPeakTime,
    bool Peaked,
    bool NewSession);
