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

    public string DebugOutputDirectory { get; init; } = "debug";
}