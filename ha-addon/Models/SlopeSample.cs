namespace SourdoughMonitor.Models;

/// <summary>A fitted rise rate (%/h) published at <paramref name="Time"/>. It describes the rate
/// at <paramref name="Centre"/>, the middle of the fitted span (older state files have none).</summary>
public sealed record SlopeSample(DateTimeOffset Time, double Slope, DateTimeOffset? Centre = null);
