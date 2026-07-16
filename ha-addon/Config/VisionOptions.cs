namespace SourdoughMonitor.Config;

public sealed class VisionOptions
{
    public int? RoiX { get; init; }

    public int? RoiY { get; init; }

    public int? RoiWidth { get; init; }

    public int? RoiHeight { get; init; }

    public double MinJarWallFraction { get; init; } = 0.08;

    public double MinJarWidthFraction { get; init; } = 0.04;

    public bool DebugSaveAnnotatedImages { get; init; } = true;

    /// <summary>Where annotated debug images and the per-frame diagnostics log are written.
    /// Defaults to the Home Assistant add-on's <c>/share</c> mount (requires
    /// <c>map: - share:rw</c> in config.yaml) so the files are reachable via Samba/File
    /// Editor and survive container rebuilds, instead of living inside the app's own
    /// (ephemeral, container-internal) install directory.</summary>
    public string DebugOutputDirectory { get; init; } = "/share/sourdough_monitor/debug";

    /// <summary>How long saved debug images and diagnostics log entries are kept before
    /// being deleted automatically, so the export folder stays a bounded, recent-only
    /// rolling window instead of growing forever.</summary>
    public double DebugRetentionHours { get; init; } = 48;
}