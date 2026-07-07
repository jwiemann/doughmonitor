namespace SourdoughMonitor.Analysis;

public sealed record LevelMeasurement(DateTimeOffset Time, double DoughTopPx, double JarTopPx, double JarBottomPx)
{
    public double DoughHeightPx => JarBottomPx - DoughTopPx;
}