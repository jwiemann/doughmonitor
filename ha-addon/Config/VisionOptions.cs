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

    public string DebugOutputDirectory { get; init; } = "debug";
}