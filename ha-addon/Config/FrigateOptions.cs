namespace SourdoughMonitor.Config;

public sealed class FrigateOptions
{
    public required string Camera { get; init; }

    public string? SnapshotUrl { get; init; }

    public string? BaseUrl { get; init; }

    public string? AccessToken { get; init; }

    /// <summary>Minutes between sampling cycles. Accepts fractional values, so sub-minute
    /// polling (e.g. 0.25 = 15s) is possible for users who want denser data.</summary>
    public double SampleIntervalMinutes { get; init; } = 5;

    /// <summary>Extra attempts within a single cycle if the snapshot fetch or detection
    /// fails, so a transient camera/network hiccup doesn't silently drop a whole interval's
    /// data point.</summary>
    public int SnapshotRetryCount { get; init; } = 2;

    public double SnapshotRetryDelaySeconds { get; init; } = 5;
}