namespace SourdoughMonitor.Config;

public sealed class AnalysisOptions
{
    public int SlopeWindowMinutes { get; init; } = 40;

    public double ResetDropFraction { get; init; } = 0.25;

    public int MinSamplesForFit { get; init; } = 8;

    public double MaxEtaRelativeStdError { get; init; } = 0.15;

    public int PeakConfirmWindows { get; init; } = 3;

    public int MaxSessionHours { get; init; } = 36;

    public string? StateFilePath { get; init; } = "sourdough_state.json";
}