namespace SourdoughMonitor.Analysis;

/// <summary>One dough-level reading in image rows. <paramref name="JarBottomPx"/> is the outer
/// edge of the glass base; <paramref name="DoughFloorPx"/> is the row where the dough body
/// actually ends, above the glass base: measured on the frame, or the glass bottom less the
/// scene's held offset. It is null until the detector knows a floor offset for the scene.</summary>
public sealed record LevelMeasurement(
    DateTimeOffset Time,
    double DoughTopPx,
    double JarTopPx,
    double JarBottomPx,
    double? DoughFloorPx = null)
{
    /// <summary>True when <see cref="DoughHeightPx"/> is measured from the dough floor.</summary>
    public bool OnDoughFloor => DoughFloorPx is not null;

    /// <summary>Dough depth in pixels, from the dough floor to the surface; from the glass
    /// bottom while no floor is known. The analyzer tracks which of the two its baseline uses.</summary>
    public double DoughHeightPx => (DoughFloorPx ?? JarBottomPx) - DoughTopPx;
}
