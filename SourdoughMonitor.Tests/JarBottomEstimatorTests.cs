using OpenCvSharp;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

/// <summary>The glass bottom on real LED-backlit frames (640x360 crops). The lit crescent of the
/// foot ends at the outer bottom edge; on the full-size frames of both scenes that edge was
/// checked by eye at row 703 / 700, i.e. 352 / 350 here.</summary>
public class JarBottomEstimatorTests
{
    private static Mat Frame(string scene, int index) =>
        Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"walls_{scene}_{index:D2}.jpg"), ImreadModes.Grayscale);

    [Theory]
    [InlineData("led_oct07", 225, 486, 352)]
    [InlineData("led_oct01", 159, 404, 350)]
    public void LitFoot_ReportsTheOuterBottomEdgeOnEveryFrame(string scene, int left, int right, int expected)
    {
        for (var i = 0; i < 14; i++)
        {
            using var gray = Frame(scene, i);
            Assert.InRange(JarBottomEstimator.Estimate(gray, left, right)!.Value, expected - 2, expected + 2);
        }
    }

    [Fact]
    public void BaseTouchingTheFrameBottom_IsNotReportedAsAnEdge()
    {
        using var gray = Frame("night_oct12", 0);
        Assert.Null(JarBottomEstimator.Estimate(gray, 128, 397));
    }

    [Fact]
    public void FlatFrame_HasNoBase()
    {
        using var gray = new Mat(360, 640, MatType.CV_8UC1, Scalar.All(120));
        Assert.Null(JarBottomEstimator.Estimate(gray, 100, 400));
    }
}
