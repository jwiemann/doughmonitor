using OpenCvSharp;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

/// <summary>Heights are measured from the dough floor, which sits above the glass base. The
/// floor is a per-scene constant the detector holds; the analyzer keeps one basis per session.</summary>
public class DoughFloorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private static RiseAnalyzer NewAnalyzer(string? statePath = null) => new(new AnalysisOptions
    {
        StateFilePath = statePath,
        MedianWindowSize = 1,
        MaxRisePxPerMinute = 1000
    });

    /// <summary>Glass bottom at row 1000, dough floor at row 980.</summary>
    private static LevelMeasurement OnFloor(DateTimeOffset time, double heightFromFloorPx) =>
        new(time, 980 - heightFromFloorPx, 0, 1000, 980);

    private static LevelMeasurement GlassBottomOnly(DateTimeOffset time, double heightFromGlassPx) =>
        new(time, 1000 - heightFromGlassPx, 0, 1000);

    private static JarLevelDetector NewDetector(string? geometryPath = null) => new(new VisionOptions
    {
        GeometryStateFilePath = geometryPath,
        DebugSaveAnnotatedImages = false
    });

    private static byte[] Frame(string name)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), ImreadModes.Color);
        Cv2.ImEncode(".jpg", image, out var bytes);
        return bytes;
    }

    /// <summary>The same scene with 80 % of its colour removed: the dough is still found, but its
    /// warm body is too faint to show where the dough floor is.</summary>
    private static byte[] FaintFrame(string name)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), ImreadModes.Color);
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using var grayColor = new Mat();
        Cv2.CvtColor(gray, grayColor, ColorConversionCodes.GRAY2BGR);
        using var faint = new Mat();
        Cv2.AddWeighted(image, 0.2, grayColor, 0.8, 0, faint);
        Cv2.ImEncode(".jpg", faint, out var bytes);
        return bytes;
    }

    [Fact]
    public void Analyze_ConvertsAGlassBottomSessionOntoTheFloorWithoutRestarting()
    {
        var analyzer = NewAnalyzer();
        var started = analyzer.Analyze(GlassBottomOnly(Start, 120));
        var grown = analyzer.Analyze(GlassBottomOnly(Start.AddMinutes(10), 150));
        Assert.Equal(25, grown!.RisePercent);

        // The same physical dough (150 px above the glass, floor 20 px higher) is 130 px deep
        // from the floor against a baseline of 100: the rise is 30 %, not 25 %.
        var converted = analyzer.Analyze(OnFloor(Start.AddMinutes(11), 130));

        Assert.NotNull(converted);
        Assert.False(converted!.NewSession);
        Assert.Equal(started!.SessionStart, converted.SessionStart);
        Assert.Equal(100, analyzer.BaselineDoughHeightPx);
        Assert.Equal(30, converted.RisePercent);
    }

    [Fact]
    public void Analyze_FloorSessionDoesNotAcceptAGlassBottomHeight()
    {
        var analyzer = NewAnalyzer();
        analyzer.Analyze(OnFloor(Start, 100));

        Assert.Null(analyzer.Analyze(GlassBottomOnly(Start.AddMinutes(1), 125)));
        var next = analyzer.Analyze(OnFloor(Start.AddMinutes(2), 110));

        Assert.NotNull(next);
        Assert.Equal(10, next!.RisePercent);
    }

    [Fact]
    public void Analyze_RestoredSessionKeepsItsHeightBasis()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"floor-state-{Guid.NewGuid():N}.json");
        try
        {
            NewAnalyzer(statePath).Analyze(OnFloor(Start, 100));

            var restored = NewAnalyzer(statePath);

            Assert.Null(restored.Analyze(GlassBottomOnly(Start.AddMinutes(1), 125)));
            Assert.Equal(10, restored.Analyze(OnFloor(Start.AddMinutes(2), 110))!.RisePercent);
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    [Theory]
    [InlineData("ambient-fed.jpg", 530, 538, 14, 22)]
    [InlineData("ambient-close.jpg", 612, 624, 28, 40)]
    public void Detector_MeasuresTheDoughFloorAboveTheGlassBase(
        string file, double floorLow, double floorHigh, double offsetLow, double offsetHigh)
    {
        var detector = NewDetector();

        var measured = detector.Measure(Frame(file), Start);

        Assert.NotNull(measured);
        Assert.InRange(measured!.DoughFloorPx!.Value, floorLow, floorHigh);
        Assert.InRange(measured.JarBottomPx - measured.DoughFloorPx.Value, offsetLow, offsetHigh);
        Assert.Equal(measured.DoughFloorPx.Value - measured.DoughTopPx, measured.DoughHeightPx);
    }

    [Fact]
    public void Detector_FaintDoughKeepsTheScenesFloorOffset()
    {
        var detector = NewDetector();
        LevelMeasurement? warm = null;
        for (var i = 0; i < 3; i++) warm = detector.Measure(Frame("ambient-fed.jpg"), Start.AddMinutes(i));

        var faint = detector.Measure(FaintFrame("ambient-fed.jpg"), Start.AddMinutes(3));

        Assert.NotNull(faint);
        Assert.Equal(warm!.DoughFloorPx!.Value, faint!.DoughFloorPx!.Value, 2.0);
    }

    [Fact]
    public void Detector_RestoresTheFloorOffsetFromPersistedGeometry()
    {
        var geometryPath = Path.Combine(Path.GetTempPath(), $"floor-geometry-{Guid.NewGuid():N}.json");
        try
        {
            var before = NewDetector(geometryPath);
            LevelMeasurement? warm = null;
            for (var i = 0; i < 3; i++) warm = before.Measure(Frame("ambient-fed.jpg"), Start.AddMinutes(i));

            var restarted = NewDetector(geometryPath);
            var faint = restarted.Measure(FaintFrame("ambient-fed.jpg"), Start.AddMinutes(10));

            Assert.NotNull(faint);
            Assert.Equal(warm!.DoughFloorPx!.Value, faint!.DoughFloorPx!.Value, 2.0);
        }
        finally
        {
            File.Delete(geometryPath);
        }
    }
}
