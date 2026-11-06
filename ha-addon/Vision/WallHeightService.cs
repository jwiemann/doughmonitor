using OpenCvSharp;

using SourdoughMonitor.Analysis;

namespace SourdoughMonitor.Vision;

/// <summary>One frame's wall-contact result in image rows, with where the height came from.</summary>
public sealed record WallHeightReading(
    double? DomeTopRow,
    double? WallLeftRow,
    double? WallRightRow,
    double? WallLevelRow,
    double? StableHeightRow,
    HeightSource HeightSource,
    DomeSource DomeSource);

/// <summary>Runs <see cref="WallContactMeasurer"/> and <see cref="StableHeightTracker"/> on a snapshot using the
/// jar walls the detector found. The detector's walls are used when their width is plausible, else the last
/// plausible ones (the classic walls fall back to a wide default by day). Shared by the live worker and replay.</summary>
public sealed class WallHeightService(WallContactOptions? options = null)
{
    private readonly WallContactOptions _options = options ?? new WallContactOptions();
    private readonly StableHeightTracker _tracker = new();
    private (int Left, int Right)? _extent;

    /// <summary>Null until a plausible jar extent is known or when the frame cannot be decoded.</summary>
    public WallHeightReading? Measure(byte[] jpegBytes, DetectionDiagnostics? diagnostics, LevelMeasurement? measurement)
    {
        if (diagnostics is { JarLeftPx: { } left, JarRightPx: { } right }
            && right - left >= _options.MinJarWidth && right - left <= _options.MaxJarWidth)
        {
            _extent = (left, right);
        }
        if (_extent is not { } extent) return null;
        using var gray = Cv2.ImDecode(jpegBytes, ImreadModes.Grayscale);
        if (gray.Empty()) return null;
        var contacts = WallContactMeasurer.Measure(gray, extent.Left, extent.Right, _options);
        var height = _tracker.Update(contacts, measurement?.DoughTopPx);
        return new WallHeightReading(
            height.DomeTopRow, contacts.LeftRow, contacts.RightRow, height.WallLevelRow, height.HeightRow,
            height.Source, height.DomeSource);
    }

    /// <summary>Forget the jar extent and smoothing (camera moved, new session).</summary>
    public void Reset()
    {
        _extent = null;
        _tracker.Reset();
    }
}
