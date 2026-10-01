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

    /// <summary>Contrast (region above vs. band interior) at which a dark band is trusted
    /// regardless of its length — the backlit case: light through the glass above, opaque
    /// dough below. Calibrated above an observed jar-base false positive (~50).</summary>
    public double StrongBandContrast { get; init; } = 55.0;

    /// <summary>Weaker contrast a dark band must still exceed to be accepted when it is
    /// long and genuinely dark (the ambient-light dough band: no backlight, the dough is
    /// simply darker than the glass/wall above it). Calibrated on a real morning scene:
    /// the dough sits only ~20 gray levels below the wall, fading over tens of rows.</summary>
    public double MinAmbientBandContrast { get; init; } = 18.0;

    /// <summary>A dark band only counts as "genuinely dark" (and thus trusted at the weaker
    /// ambient contrast) when its interior mean stays below this gray level. The observed
    /// jar-base shadow (~150) fails this; real dough (~105-131) passes.</summary>
    public double DarkBandMaxIntensity { get; init; } = 135.0;

    /// <summary>Minimum length of a "genuinely dark" band as a fraction of the column
    /// height. Real dough fills a substantial part of the jar; brief dark dips (jar base
    /// shadow, lettering) do not.</summary>
    public double MinDarkBandFraction { get; init; } = 0.2;

    /// <summary>Primary surface signal under ambient light: the dough is warm-toned
    /// (tan) while glass/wall/background are neutral. A warm band qualifies when its
    /// saturation exceeds the neutral glass reference by at least this step.
    /// Validated on real frames: glass sat 2-5, dough sat 25-32.</summary>
    public double MinWarmSaturationStep { get; init; } = 12.0;

    /// <summary>A short warm band must exceed this saturation step to be accepted —
    /// set above the observed warm table/wood signal (~13-15) so background wood never
    /// passes, while real dough (25+) does even as a thin layer.</summary>
    public double StrongWarmSaturation { get; init; } = 22.0;

    /// <summary>The warm-band method only applies when the glass/wall reference level
    /// (top-quarter modal saturation) is at most this neutral. A strongly tinted scene
    /// (warm room light everywhere) has no neutral reference and falls through to the
    /// brightness band method.</summary>
    public double MaxNeutralReferenceSaturation { get; init; } = 10.0;

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