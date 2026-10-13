using OpenCvSharp;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

/// <summary>The wall tracker on real LED-backlit scenes of the archive (640x360 crops of 14
/// consecutive-ish frames each). The expected walls were checked by drawing the fitted lines
/// over the frames: they sit on the glass edges, tapered inwards towards the bottom.</summary>
public class JarWallTrackerTests
{
    private static JarWalls? Track(string scene, double minScore = 0.6)
    {
        var tracker = new JarWallTracker(minScore);
        for (var i = 0; ; i++)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"walls_{scene}_{i:D2}.jpg");
            if (!File.Exists(path)) break;
            using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
            tracker.Update(gray);
        }
        return tracker.Confident;
    }

    [Theory]
    [InlineData("night_oct12", 121.4, 401.6, 0.14, -0.09)]
    [InlineData("led_oct07", 219.2, 492.4, 0.12, -0.11)]
    [InlineData("led_oct01", 152.8, 408.6, 0.13, -0.09)]
    public void ClearlyLitScene_FindsTheAngledGlassWalls(
        string scene, double leftAtRow250, double rightAtRow250, double leftSlope, double rightSlope)
    {
        var walls = Track(scene);

        Assert.NotNull(walls);
        Assert.InRange(walls!.LeftAt(250), leftAtRow250 - 6, leftAtRow250 + 6);
        Assert.InRange(walls.RightAt(250), rightAtRow250 - 6, rightAtRow250 + 6);
        Assert.InRange(walls.LeftSlope, leftSlope - 0.03, leftSlope + 0.03);
        Assert.InRange(walls.RightSlope, rightSlope - 0.03, rightSlope + 0.03);
        // The jar narrows towards the bottom: the angle is real, not noise.
        Assert.True(walls.RightAt(330) - walls.LeftAt(330) < walls.RightAt(30) - walls.LeftAt(30) - 20);
    }

    [Fact]
    public void ConfidenceThreshold_WithholdsTheModelFromWeakEvidence()
    {
        Assert.Null(Track("night_oct12", minScore: 5.0));
    }

    [Fact]
    public void FewerFramesThanTheMinimum_GiveNoModel()
    {
        var tracker = new JarWallTracker(0.6);
        for (var i = 0; i < 5; i++)
        {
            using var gray = Cv2.ImRead(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", $"walls_night_oct12_{i:D2}.jpg"), ImreadModes.Grayscale);
            tracker.Update(gray);
        }
        Assert.Null(tracker.Confident);
    }
}
