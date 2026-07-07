namespace SourdoughMonitor.Config;

public sealed class FrigateOptions
{
    public required string Camera { get; init; }
    public string? SnapshotUrl { get; init; }
    public string? BaseUrl { get; init; }
    public string? AccessToken { get; init; }
    public int SampleIntervalMinutes { get; init; } = 10;
}