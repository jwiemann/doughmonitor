using SourdoughMonitor.Analysis;

namespace SourdoughMonitor.Vision;

/// <summary>A dough-level detector the worker can run on a snapshot: the vision-LLM detector and
/// the classic image-pattern detector share this surface so the worker can switch between them.</summary>
public interface IJarDetector
{
    /// <summary>Raised when the camera view changed; the rise baseline no longer applies.</summary>
    event Action? SceneChanged;

    DetectionDiagnostics? LastDiagnostics { get; }

    /// <summary>Outcome of the most recent measurement ("detected", "dark_frame", "no_surface", ...).</summary>
    string LastOutcome { get; }

    byte[]? LatestAnnotatedImageBytes { get; }

    Task<LevelMeasurement?> MeasureAsync(
        byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx, CancellationToken ct);
}
