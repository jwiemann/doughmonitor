using OpenCvSharp;

using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

/// <summary>The bread bowl of 2026-11-12: the dough is a black silhouette (or a darker body in a lit room)
/// under a flour-dusted glass interior; the vision model's level was 304-320 px and once 354.</summary>
public class SilhouetteRefinerTests
{
    private static readonly JarBox Bowl = new(130, 200, 1150, 640);

    [Theory]
    [InlineData("bowl-dark.jpg", 318)]
    [InlineData("bowl-dark.jpg", 354)]
    [InlineData("bowl-room-lit.jpg", 354)]
    public void LevelSnapsToTheDoughEdge_WhateverTheModelSaid(string file, double modelRow)
    {
        using var color = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file), ImreadModes.Color);
        using var gray = new Mat();
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);

        var row = SilhouetteRefiner.Refine(gray, Bowl, 0, 0, modelRow);

        Assert.NotNull(row);
        Assert.InRange(row!.Value, 288, 302);
    }

    [Fact]
    public void NoEdgeNearTheModelLevel_KeepsTheModel()
    {
        using var flat = new Mat(720, 1280, MatType.CV_8UC1, Scalar.All(120));

        Assert.Null(SilhouetteRefiner.Refine(flat, Bowl, 0, 0, 320));
    }
}
