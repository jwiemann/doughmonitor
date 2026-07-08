namespace SourdoughMonitor.Vision;

public sealed record DetectionDiagnostics(string Method, double BandContrast, int? BandTopRow, int? FinalRow);