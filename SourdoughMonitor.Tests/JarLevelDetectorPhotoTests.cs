using OpenCvSharp;

using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class JarLevelDetectorPhotoTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 2, 43, 0, TimeSpan.Zero);

    private static JarLevelDetector NewDetector() => new(new VisionOptions
    {
        GeometryStateFilePath = null,
        DebugSaveAnnotatedImages = false
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
    public void ResolutionChange_InvalidatesPixelGrowthBaseline()
    {
        var detector = NewDetector();
        Assert.NotNull(detector.Measure(Frame("backlit-night.jpg"), Start));
        var changes = 0;
        detector.SceneChanged += () => changes++;
        var resized = detector.Measure(Frame("backlit-night.jpg", 0.5), Start.AddMinutes(10));
        Assert.NotNull(resized);
        Assert.Equal(1, changes);
        Assert.InRange(resized!.DoughTopPx, 240, 255);
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
