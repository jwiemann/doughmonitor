namespace SourdoughMonitor.Vision;

public sealed record DetectionDiagnostics(string Method, double BandContrast, int? BandTopRow, int? FinalRow)
{
    /// <summary>Mean gray intensity of the analyzed region (ROI). Null when the frame was undecodable.</summary>
    public double? FrameMean { get; init; }

    /// <summary>Median gray intensity of the analyzed region (ROI). Drives Canny thresholds.</summary>
    public double? FrameMedian { get; init; }

    /// <summary>10th percentile gray intensity of the analyzed region. Gate input.</summary>
    public double? FrameP10 { get; init; }

    /// <summary>90th percentile gray intensity of the analyzed region. Gate input: a frame
    /// is usable when it contains a sufficiently bright region (lit jar) even if the room
    /// around it is dark (night with backlight).</summary>
    public double? FrameP90 { get; init; }

    /// <summary>Jar geometry used for this frame, including failed surface searches.</summary>
    public int? JarLeftPx { get; init; }

    public int? JarRightPx { get; init; }

    public string? JarColumnKind { get; init; }
}