using OpenCvSharp;

using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class JarLevelDetectorDarkGateTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static VisionOptions ReplayVisionOptions() => new()
    {
        MinFrameIntensity = 25.0,
        MinFrameContrast = 30.0,
        DebugSaveAnnotatedImages = false
    };

    /// <summary>Synthetic backlit jar: bright glass column (x 40-80) over a dark dough band
    /// below y=100, dim room background. Mirrors the production scene.</summary>
    private static byte[] SyntheticJarJpeg(double brightness)
    {
        using var img = new Mat(160, 120, MatType.CV_8UC1);
        for (var y = 0; y < img.Rows; y++)
        for (var x = 0; x < img.Cols; x++)
        {
            byte gray = (byte)(x is >= 40 and < 80
                ? y < 100 ? 190 : 30
                : 20);
            img.Set(y, x, (byte)(gray * brightness));
        }
        Cv2.ImEncode(".jpg", img, out var bytes);
        return bytes;
    }

    [Fact]
    public void Measure_BacklitJar_DetectsDoughSurfaceAtBandTop()
    {
        var detector = new JarLevelDetector(ReplayVisionOptions());
        var measurement = detector.Measure(SyntheticJarJpeg(1.0), FixedTime);
        Assert.NotNull(measurement);
        Assert.Equal("detected", detector.LastOutcome);
        Assert.InRange(measurement!.DoughTopPx, 98, 102);
        Assert.NotNull(detector.LastDiagnostics);
        Assert.True(detector.LastDiagnostics!.FrameMedian >= 12);
    }

    [Fact]
    public void Measure_DarkNightFrame_RejectedWithDarkOutcome()
    {
        var detector = new JarLevelDetector(ReplayVisionOptions());
        var measurement = detector.Measure(SyntheticJarJpeg(0.05), FixedTime);
        Assert.Null(measurement);
        Assert.Equal("dark_frame", detector.LastOutcome);
        var diagnostics = detector.LastDiagnostics;
        Assert.NotNull(diagnostics);
        Assert.Equal("dark", diagnostics!.Method);
        Assert.True(diagnostics.FrameP90 < 25);
    }

    [Fact]
    public void Measure_LitUniformFrameAfterAJar_IsAnEmptyScene()
    {
        // The jar scene, then an all-gray bright frame: P90 high, P90-P10 ~ 0, and nothing like the
        // frame before: the jar was taken away, it is not night.
        var detector = new JarLevelDetector(ReplayVisionOptions());
        Assert.NotNull(detector.Measure(SyntheticJarJpeg(1.0), FixedTime));
        using var img = new Mat(160, 120, MatType.CV_8UC1, Scalar.All(200));
        Cv2.ImEncode(".jpg", img, out var bytes);

        Assert.Null(detector.Measure(bytes, FixedTime.AddMinutes(5)));
        Assert.Equal("empty_scene", detector.LastOutcome);
    }

    [Fact]
    public void Measure_JarInFlatLight_IsNotAnEmptyScene()
    {
        // The same jar washed out to a bright, low-contrast frame (flat daylight, auto exposure):
        // as uniform as an empty shelf, but still the same scene.
        var detector = new JarLevelDetector(ReplayVisionOptions());
        Assert.NotNull(detector.Measure(SyntheticJarJpeg(1.0), FixedTime));
        using var flat = new Mat(160, 120, MatType.CV_8UC1);
        for (var y = 0; y < flat.Rows; y++)
        for (var x = 0; x < flat.Cols; x++)
            flat.Set(y, x, (byte)(x is >= 40 and < 80 ? y < 100 ? 140 : 118 : 112));
        Cv2.ImEncode(".jpg", flat, out var bytes);

        Assert.Null(detector.Measure(bytes, FixedTime.AddMinutes(5)));
        Assert.Equal("dark_frame", detector.LastOutcome);
    }

    [Fact]
    public void Measure_UniformFrameWithoutAnEarlierScene_IsADarkFrame()
    {
        using var img = new Mat(160, 120, MatType.CV_8UC1, Scalar.All(200));
        Cv2.ImEncode(".jpg", img, out var bytes);
        var detector = new JarLevelDetector(ReplayVisionOptions());
        Assert.Null(detector.Measure(bytes, FixedTime));
        Assert.Equal("dark_frame", detector.LastOutcome);
    }

    [Fact]
    public void Measure_DimUniformFrame_StaysADarkFrame()
    {
        var detector = new JarLevelDetector(ReplayVisionOptions());
        Assert.NotNull(detector.Measure(SyntheticJarJpeg(1.0), FixedTime));
        using var img = new Mat(160, 120, MatType.CV_8UC1, Scalar.All(40));
        Cv2.ImEncode(".jpg", img, out var bytes);
        Assert.Null(detector.Measure(bytes, FixedTime.AddMinutes(5)));
        Assert.Equal("dark_frame", detector.LastOutcome);
    }

    [Fact]
    public void Measure_GateDisabled_ProcessesDarkFrame()
    {
        var detector = new JarLevelDetector(new VisionOptions
        {
            MinFrameIntensity = 0.0,
            MinFrameContrast = 0.0,
            DebugSaveAnnotatedImages = false
        });
        var measurement = detector.Measure(SyntheticJarJpeg(0.05), FixedTime);
        Assert.NotEqual("dark_frame", detector.LastOutcome);
    }
}
