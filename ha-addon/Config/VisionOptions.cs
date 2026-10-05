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

    /// <summary>Primary surface signal under ambient light: the dough is warm-toned
    /// (tan) while glass/wall/background are neutral. A warm pixel counts when its
    /// saturation exceeds the neutral glass reference by at least this step.
    /// Calibrated against the user-confirmed dough surface: step 9 (sat ≥ 12) lands the
    /// front edge on the confirmed boundary; step 12 (sat ≥ 15) read ~50 px too deep
    /// (the dough's upper skin is less saturated than its body).</summary>
    public double MinWarmSaturationStep { get; init; } = 9.0;

    /// <summary>Column-level warmth for the jar-extent detection: a zone row counts as warm
    /// when its saturation exceeds the neutral top-quarter reference by this step.
    /// Calibrated on real frames (6 separates glass from wall despite neutral white
    /// reflection streaks on the glass flanks; 12 loses ~80px of jar on each side).</summary>
    public double WarmColumnSaturationStep { get; init; } = 6.0;

    /// <summary>Upper bound for the neutral saturation reference measured from the frame's
    /// top quarter. The condensation/smear zone above the dough sits in that quarter and
    /// pollutes the reference (observed 8-10 instead of the clean-glass 2-3), silently
    /// stiffening the warm mask past its calibrated threshold — the surface crossing then
    /// wanders between the dough skin and the saturated body. The cap pins the effective
    /// mask threshold (reference + MinWarmSaturationStep) to the user-confirmed level
    /// (~11 on the max−min spread) on every frame regardless of smear wetness.</summary>
    public double NeutralReferenceCeiling { get; init; } = 2.0;

    /// <summary>Minimum fraction of warm zone rows for a column to count as jar interior.
    /// Calibrated on real frames (0.55); the warm table strip below the jar stays far
    /// below it.</summary>
    public double WarmColumnMinFraction { get; init; } = 0.55;

    /// <summary>Minimum length of a "genuinely dark" band as a fraction of the column
    /// height. Real dough fills a substantial part of the jar; brief dark dips (jar base
    /// shadow, lettering) do not.</summary>
    public double MinDarkBandFraction { get; init; } = 0.2;

    /// <summary>Warm-coverage crossing used only to anchor the surface search window.
    /// The calibrated 0.6 crossing includes the pale upper dough; larger fractions
    /// select its saturated interior. The final level is the traced luminance boundary,
    /// not this crossing (confirmed daylight reference: about row 475).</summary>
    public double FrontEdgeCoverageFraction { get; init; } = 0.6;

    /// <summary>Persists verified column bounds, the reference image dimensions and the
    /// scene's physical jar base. Restart thumbnails retain the same measurement units;
    /// supported geometry changes can still reset the analyzer. Null disables persistence.</summary>
    public string? GeometryStateFilePath { get; init; } = "jar_geometry.json";

    public bool DebugSaveAnnotatedImages { get; init; } = true;

    /// <summary>Paints the color filter's dough mask (warm tone) translucently into the
    /// debug image. Off by default: the overlay makes it hard to tell shadows from actual
    /// dough when inspecting the raw scene. Turn on in the add-on options
    /// (`debug_highlight_dough`) when tuning the detection.</summary>
    public bool DebugHighlightDough { get; init; }

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