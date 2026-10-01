namespace SourdoughMonitor.Config;

public sealed class VisionOptions
{
    public int? RoiX { get; init; }

    public int? RoiY { get; init; }

    public int? RoiWidth { get; init; }

    public int? RoiHeight { get; init; }

    public double MinJarWallFraction { get; init; } = 0.08;

    public double MinJarWidthFraction { get; init; } = 0.04;

    /// <summary>Frames whose 90th percentile ROI intensity is below this are rejected as
    /// "dark" (night without backlight): nothing in the frame is bright enough to be a lit
    /// jar, and detection would feed noise into the growth series. Percentile-based so a
    /// backlit night frame (dark room, lit jar) still passes. 0 disables the floor.</summary>
    public double MinFrameIntensity { get; init; } = 25.0;

    /// <summary>Frames whose ROI intensity spread (P90 - P10) is below this are rejected:
    /// a dim but uniform frame carries no meaningful contrast for detection. 0 disables.</summary>
    public double MinFrameContrast { get; init; } = 30.0;

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