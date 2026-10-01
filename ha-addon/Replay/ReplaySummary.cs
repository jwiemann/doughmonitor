using SourdoughMonitor.Analysis;

namespace SourdoughMonitor.Replay;

/// <summary>Aggregate result of a replay run, written as summary.json.</summary>
public sealed record ReplaySummary(
    string InputFolder,
    string? ConfigPath,
    (int X, int Y, int W, int H)? RoiApplied,
    int FrameCount,
    int Detected,
    int DarkFrames,
    int NoSurface,
    int DecodeFailed,
    int GateRejected,
    int MtimeFallback,
    DateTimeOffset FirstTime,
    DateTimeOffset LastTime,
    Dictionary<string, int> MethodCounts,
    GrowthAnalysis? Growth,
    RiseReading? LastRiseReading,
    List<string> Warnings);
