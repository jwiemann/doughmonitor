using System.Text.Json.Nodes;

using OpenCvSharp;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class JarLevelDetectorPhotoTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 2, 43, 0, TimeSpan.Zero);

    private static JarLevelDetector NewDetector(string? geometryPath = null) => new(new VisionOptions
    {
        GeometryStateFilePath = geometryPath,
        DebugSaveAnnotatedImages = false
    });

    private static RiseAnalyzer NewAnalyzer(string? statePath = null) => new(new AnalysisOptions
    {
        StateFilePath = statePath,
        MedianWindowSize = 1
    });

    private static byte[] Frame(string name, double scale = 1)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        using var original = Cv2.ImRead(path, ImreadModes.Color);
        using var resized = new Mat();
        Cv2.Resize(original, resized, new Size(), scale, scale, InterpolationFlags.Area);
        Cv2.ImEncode(".jpg", resized, out var bytes);
        return bytes;
    }

    [Theory]
    [InlineData("backlit-night.jpg", 0.5, 480, 510)]
    [InlineData("backlit-night.jpg", 1.0, 480, 510)]
    [InlineData("backlit-night.jpg", 1.25, 480, 510)]
    [InlineData("backlit-day.jpg", 0.5, 485, 515)]
    [InlineData("backlit-day.jpg", 1.0, 485, 515)]
    [InlineData("backlit-day.jpg", 1.25, 485, 515)]
    public void Backlight_MeasuresDoughFrontInsteadOfHotspotOrWall(
        string file, double scale, double low, double high)
    {
        var detector = NewDetector();
        var measured = detector.Measure(Frame(file, scale), Start);
        Assert.NotNull(measured);
        Assert.InRange(measured!.DoughTopPx / scale, low, high);
        Assert.InRange(measured.JarBottomPx / scale, 686, 711);
        Assert.InRange(detector.LastDiagnostics!.JarLeftPx!.Value / scale, 275, 360);
        Assert.InRange(detector.LastDiagnostics.JarRightPx!.Value / scale, 765, 855);
    }

    [Theory]
    [InlineData("backlit-night.jpg", 480, 510)]
    [InlineData("wide-day.jpg", 420, 445)]
    [InlineData("backlit-day.jpg", 485, 515)]
    public void SmallerJar_RemainsDetectableWithoutCoveringMostOfFrame(string file, double low, double high)
    {
        using var original = Cv2.ImDecode(Frame(file), ImreadModes.Color);
        using var smaller = new Mat();
        Cv2.Resize(original, smaller, new Size(), 0.5, 0.5, InterpolationFlags.Area);
        using var canvas = new Mat(original.Rows, original.Cols, MatType.CV_8UC3, Scalar.All(32));
        var x = (canvas.Width - smaller.Width) / 2;
        var y = (canvas.Height - smaller.Height) / 2;
        using var target = canvas[new Rect(x, y, smaller.Width, smaller.Height)];
        smaller.CopyTo(target);
        Cv2.ImEncode(".jpg", canvas, out var bytes);
        var detector = NewDetector();
        var measured = detector.Measure(bytes, Start);
        Assert.NotNull(measured);
        Assert.InRange(measured!.DoughTopPx, y + low * 0.5, y + high * 0.5);
    }

    [Fact]
    public void WideView_UsesVisibleJarBaseInsteadOfFrameBottom()
    {
        var detector = NewDetector();
        var measured = detector.Measure(Frame("wide-day.jpg"), Start);
        Assert.NotNull(measured);
        Assert.InRange(measured!.DoughTopPx, 420, 445);
        Assert.InRange(measured.JarBottomPx, 620, 654);
    }

    [Fact]
    public void PaleDough_DoesNotMeasureTheSaturatedInteriorAsSurface()
    {
        var detector = NewDetector();
        var measured = detector.Measure(Frame("pale-day.jpg"), Start);
        Assert.NotNull(measured);
        Assert.InRange(measured!.DoughTopPx, 455, 495);
        Assert.InRange(measured.JarBottomPx, 680, 707);
    }

    [Fact]
    public void BacklightTransition_ReflectionBandDoesNotBecomeJarBottom()
    {
        var detector = NewDetector();
        var measured = detector.Measure(Frame("led-transition.jpg"), Start);
        Assert.NotNull(measured);
        Assert.InRange(measured!.JarBottomPx, 686, 711);
        Assert.InRange(measured.DoughTopPx, 480, 565);
        Assert.True(measured.DoughHeightPx > 100);
    }

    [Fact]
    public void EmptyShelf_DoesNotCreateDoughOrJarMeasurement()
    {
        var detector = NewDetector();
        Assert.Null(detector.Measure(Frame("empty-shelf.jpg"), Start));
    }

    [Fact]
    public void EmptyWoodenStand_DoesNotCreateDoughOrJarMeasurement()
    {
        var detector = NewDetector();
        Assert.Null(detector.Measure(Frame("empty-wooden-stand.jpg"), Start));
    }

    [Fact]
    public void EmptyWoodenStand_DoesNotPoisonAnEstablishedSession()
    {
        var detector = NewDetector();
        var analyzer = NewAnalyzer();
        detector.SceneChanged += () => analyzer.Reset();
        var fed = detector.Measure(Frame("ambient-fed.jpg"), Start);
        Assert.NotNull(fed);
        var initial = analyzer.Analyze(fed!);
        Assert.NotNull(initial);
        Assert.Null(detector.Measure(Frame("empty-wooden-stand.jpg"), Start.AddMinutes(1),
            analyzer.BaselineDoughHeightPx));
        var returned = detector.Measure(Frame("ambient-fed.jpg"), Start.AddMinutes(2),
            analyzer.BaselineDoughHeightPx);
        Assert.NotNull(returned);
        var reading = analyzer.Analyze(returned!);
        Assert.NotNull(reading);
        Assert.False(reading!.NewSession);
        Assert.Equal(initial!.SessionStart, reading.SessionStart);
        Assert.Equal(fed!.DoughHeightPx, analyzer.BaselineDoughHeightPx);
        Assert.InRange(reading.RisePercent, 0, 4);
    }

    [Theory]
    [InlineData("ambient-fed.jpg", "ambient-grown.jpg", "ambient-dawn-", 60, 80)]
    [InlineData("ambient-prior-fed-clear.jpg", "ambient-prior-grown.jpg", "ambient-cast-dawn-", 55, 66)]
    public void DawnReacquisition_PreservesFeedingBaselineAcrossLightingGap(
        string fedFrame, string grownFrame, string dawnPrefix, double minimumRise, double maximumRise)
    {
        var detector = NewDetector();
        var analyzer = NewAnalyzer();
        detector.SceneChanged += () => analyzer.Reset();
        var fed = detector.Measure(Frame(fedFrame), Start);
        Assert.NotNull(fed);
        var initial = analyzer.Analyze(fed!);
        var time = Start.AddHours(13);
        for (var i = 1; i <= 3; i++)
        {
            var measurement = detector.Measure(Frame($"{dawnPrefix}{i}.jpg"), time, analyzer.BaselineDoughHeightPx);
            if (measurement is not null) analyzer.Analyze(measurement);
            time = time.AddMinutes(1);
        }
        var grown = detector.Measure(Frame(grownFrame), time.AddMinutes(40), analyzer.BaselineDoughHeightPx);
        Assert.NotNull(grown);
        var reading = analyzer.Analyze(grown!);
        Assert.NotNull(reading);
        Assert.False(reading!.NewSession);
        Assert.Equal(initial!.SessionStart, reading.SessionStart);
        Assert.Equal(fed!.DoughHeightPx, analyzer.BaselineDoughHeightPx);
        Assert.InRange(reading.RisePercent, minimumRise, maximumRise);
    }


    [Fact]
    public void ResolutionChange_PreservesBaselineCoordinatesAndDoesNotResetTheSession()
    {
        var detector = NewDetector();
        var analyzer = NewAnalyzer();
        detector.SceneChanged += () => analyzer.Reset();
        var full = detector.Measure(Frame("backlit-night.jpg"), Start);
        Assert.NotNull(full);
        var initial = analyzer.Analyze(full!);
        var resized = detector.Measure(Frame("backlit-night.jpg", 0.25), Start.AddMinutes(10),
            analyzer.BaselineDoughHeightPx);
        Assert.NotNull(resized);
        var smallReading = analyzer.Analyze(resized!);
        Assert.NotNull(smallReading);
        Assert.False(smallReading!.NewSession);
        Assert.Equal(initial!.SessionStart, smallReading.SessionStart);
        Assert.InRange(smallReading.RisePercent, 0, 6);
        var restored = detector.Measure(Frame("backlit-night.jpg"), Start.AddMinutes(20),
            analyzer.BaselineDoughHeightPx);
        Assert.NotNull(restored);
        var reading = analyzer.Analyze(restored!);
        Assert.NotNull(reading);
        Assert.False(reading!.NewSession);
        Assert.Equal(full!.DoughHeightPx, analyzer.BaselineDoughHeightPx);
        Assert.Equal(initial.SessionStart, reading.SessionStart);
        Assert.InRange(reading.RisePercent, 0, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartWithThumbnail_PreservesPersistedFeedingCoordinates(bool legacyGeometry)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sourdough_geometry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var geometry = Path.Combine(directory, "geometry.json");
        var state = Path.Combine(directory, "state.json");
        try
        {
            var start = DateTimeOffset.UtcNow;
            var detector = NewDetector(geometry);
            var analyzer = NewAnalyzer(state);
            detector.SceneChanged += () => analyzer.Reset();
            var original = detector.Measure(Frame("backlit-night.jpg"), start);
            Assert.NotNull(original);
            var initial = analyzer.Analyze(original!);
            if (legacyGeometry)
            {
                var saved = JsonNode.Parse(File.ReadAllText(geometry))!.AsObject();
                saved.Remove("frame_width");
                saved.Remove("frame_height");
                saved.Remove("jar_bottom_px");
                File.WriteAllText(geometry, saved.ToJsonString());
            }
            var restoredDetector = NewDetector(geometry);
            var restoredAnalyzer = NewAnalyzer(state);
            restoredDetector.SceneChanged += () => restoredAnalyzer.Reset();
            var thumbnail = restoredDetector.Measure(Frame("backlit-night.jpg", 0.25),
                start.AddMinutes(1), restoredAnalyzer.BaselineDoughHeightPx);
            if (thumbnail is not null)
            {
                var smallReading = restoredAnalyzer.Analyze(thumbnail);
                Assert.NotNull(smallReading);
                Assert.False(smallReading!.NewSession);
                Assert.InRange(smallReading.RisePercent, 0, 6);
            }
            var full = restoredDetector.Measure(Frame("backlit-night.jpg"), start.AddMinutes(2),
                restoredAnalyzer.BaselineDoughHeightPx);
            Assert.NotNull(full);
            var reading = restoredAnalyzer.Analyze(full!);
            Assert.NotNull(reading);
            Assert.False(reading!.NewSession);
            Assert.Equal(initial!.SessionStart, reading.SessionStart);
            Assert.Equal(original!.DoughHeightPx, restoredAnalyzer.BaselineDoughHeightPx);
            Assert.InRange(reading.RisePercent, 0, 4);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidatedCameraMove_StartsANewSessionAfterConfirmation()
    {
        var detector = NewDetector();
        var analyzer = NewAnalyzer();
        detector.SceneChanged += () => analyzer.Reset();
        var original = detector.Measure(Frame("backlit-night.jpg"), Start);
        Assert.NotNull(original);
        analyzer.Analyze(original!);
        using var source = Cv2.ImDecode(Frame("backlit-night.jpg"), ImreadModes.Color);
        RiseReading? reading = null;
        for (var i = 0; i < 3; i++)
        {
            using var transform = new Mat(2, 3, MatType.CV_64FC1);
            transform.Set(0, 0, 1.0); transform.Set(0, 1, 0.0); transform.Set(0, 2, 160.0 + i);
            transform.Set(1, 0, 0.0); transform.Set(1, 1, 1.0); transform.Set(1, 2, -80.0);
            using var shifted = new Mat();
            Cv2.WarpAffine(source, shifted, transform, source.Size(),
                InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(32));
            Cv2.ImEncode(".jpg", shifted, out var bytes);
            var measurement = detector.Measure(bytes, Start.AddMinutes(i + 1), analyzer.BaselineDoughHeightPx);
            if (measurement is not null) reading = analyzer.Analyze(measurement);
        }
        Assert.NotNull(reading);
        Assert.True(reading!.NewSession);
        Assert.Equal(Start.AddMinutes(3), reading.SessionStart);
    }

    [Theory]
    [InlineData("backlit-night.jpg")]
    [InlineData("backlit-day.jpg")]
    public void TemporaryDownsize_DoesNotReacquireTheWallAsJar(string file)
    {
        var detector = NewDetector();
        Assert.NotNull(detector.Measure(Frame(file), Start));
        var initialLeft = detector.LastDiagnostics!.JarLeftPx!.Value;
        var initialRight = detector.LastDiagnostics.JarRightPx!.Value;
        Assert.NotNull(detector.Measure(Frame(file, 0.25), Start.AddMinutes(1)));
        var restored = detector.Measure(Frame(file), Start.AddMinutes(2));
        Assert.NotNull(restored);
        Assert.InRange(Math.Abs(detector.LastDiagnostics!.JarLeftPx!.Value - initialLeft), 0, 4);
        Assert.InRange(Math.Abs(detector.LastDiagnostics.JarRightPx!.Value - initialRight), 0, 4);
        Assert.InRange(restored!.DoughTopPx, 480, 515);
    }
}
