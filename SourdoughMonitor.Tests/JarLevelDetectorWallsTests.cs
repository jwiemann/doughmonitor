using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

/// <summary>The detector feeds every usable frame to the wall tracker and exposes the model.</summary>
public class JarLevelDetectorWallsTests
{
    [Fact]
    public void LedBacklitFrames_EstablishTheAngledWallModel()
    {
        var detector = new JarLevelDetector(new VisionOptions
        {
            GeometryStateFilePath = null,
            DebugSaveAnnotatedImages = false
        });
        var start = new DateTimeOffset(2026, 10, 7, 21, 0, 0, TimeSpan.Zero);
        Assert.Null(detector.Walls);

        for (var i = 0; i < 14; i++)
        {
            var bytes = File.ReadAllBytes(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", $"walls_led_oct07_{i:D2}.jpg"));
            detector.Measure(bytes, start.AddMinutes(i));
        }

        var walls = detector.Walls;
        Assert.NotNull(walls);
        Assert.InRange(walls!.LeftAt(250), 219.2 - 8, 219.2 + 8);
        Assert.InRange(walls.RightAt(250), 492.4 - 8, 492.4 + 8);
        Assert.True(walls.LeftSlope > 0.05 && walls.RightSlope < -0.05);
    }
}
