namespace SourdoughMonitor.Config;

public sealed class FrigateOptions
{
    public required string Camera { get; init; }

    public string? SnapshotUrl { get; init; }

    public string? BaseUrl { get; init; }

    public string? AccessToken { get; init; }

    /// <summary>When set, every fetched snapshot is archived to this directory as
    /// yyyyMMdd_HHmmssfff.jpg — the raw frame record for offline replay analysis.
    /// Absolute path, or relative to the app base directory. Empty = archiving off.</summary>
    public string? SnapshotArchiveDirectory { get; init; }

    public int SampleIntervalMinutes { get; init; } = 10;
}