namespace SourdoughMonitor.Config;

public sealed class GrowthOptions
{
    public double MaxRisePxPerMinute { get; init; } = 4.0;

    public double JitterTolerancePx { get; init; } = 6.0;

    public int MedianWindowSize { get; init; } = 5;

    public TimeSpan RateWindow { get; init; } = TimeSpan.FromMinutes(30);

    public double FlatRatePxPerHour { get; init; } = 5.0;

    public int MinSamplesForFit { get; init; } = 10;

    public double PeakFraction { get; init; } = 0.97;

    public double TargetGrowthFactor { get; init; } = 1.75;

    public TimeSpan MaxEtaHorizon { get; init; } = TimeSpan.FromHours(16);

    public TimeSpan HistoryRetention { get; init; } = TimeSpan.FromHours(24);
}