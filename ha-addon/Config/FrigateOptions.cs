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

    /// <summary>Minutes between sampling cycles. Accepts fractional values, so sub-minute
    /// polling (e.g. 0.25 = 15s) is possible for users who want denser data.</summary>
    public double SampleIntervalMinutes { get; init; } = 1;

    /// <summary>Extra attempts within a single cycle when the snapshot FETCH fails, so a
    /// transient network hiccup doesn't silently drop a whole interval's data point.
    /// Kept low: every fetch wakes (battery) cameras and load is what breaks their live
    /// stream. Detection misses are not retried — the next cycle samples again anyway.</summary>
    public int SnapshotRetryCount { get; init; } = 1;

    public double SnapshotRetryDelaySeconds { get; init; } = 5;

    /// <summary>JPEG quality for snapshots fetched via Frigate's
    /// <c>/api/&lt;camera&gt;/latest.jpg</c> (BaseUrl mode). Lower values cut the
    /// JPEG-encode CPU on the Frigate host — relevant when Frigate runs on a small box
    /// like a Raspberry Pi that also serves the live stream. Detection works fine on
    /// smaller frames (all thresholds are relative).</summary>
    public int SnapshotQuality { get; init; } = 75;

    /// <summary>Snapshot height for Frigate's latest.jpg (aspect ratio preserved). Lower
    /// = less CPU and bandwidth on the Frigate host.</summary>
    public int SnapshotHeight { get; init; } = 720;
}
