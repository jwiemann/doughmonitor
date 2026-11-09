using OpenCvSharp;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class JarGeometryEstimatorTests
{
    private const int Width = 1280;
    private const int Height = 720;

    // Truth: walls leaning in towards the bottom by different amounts (a tilted, tapered jar).
    private static double TrueLeft(double y) => 330 + 0.12 * y;
    private static double TrueRight(double y) => 960 - 0.07 * y;

    private static Mat DoughFrame()
    {
        var frame = new Mat(Height, Width, MatType.CV_8UC1, Scalar.All(190));
        for (var y = 430; y < 700; y++)
            Cv2.Line(frame, new Point((int)TrueLeft(y) + 4, y), new Point((int)TrueRight(y) - 4, y), Scalar.All(50));
        return frame;
    }

    [Fact]
    public void DoughSilhouette_GivesTiltedWallsEvenFromAnInexactPrior()
    {
        var estimator = new JarGeometryEstimator();
        // The tracker's guess is off by 25 px on both sides and has the wrong angle.
        var prior = new JarWalls(
            TrueLeft(360) + 25, 0.05, TrueRight(360) - 25, -0.02, 360, 0.3, 12);
        using var frame = DoughFrame();

        for (var i = 0; i < 6; i++) estimator.Observe(frame, 430, 700, prior);

        var walls = estimator.Walls;
        Assert.NotNull(walls);
        Assert.InRange(walls!.LeftAt(500), TrueLeft(500) - 6, TrueLeft(500) + 6);
        Assert.InRange(walls.RightAt(500), TrueRight(500) - 6, TrueRight(500) + 6);
        Assert.InRange(walls.LeftSlope, 0.12 - 0.02, 0.12 + 0.02);
        Assert.InRange(walls.RightSlope, -0.07 - 0.02, -0.07 + 0.02);
    }

    [Fact]
    public void ContradictingFrames_GiveNoModel()
    {
        var estimator = new JarGeometryEstimator();
        var prior = new JarWalls(TrueLeft(360), 0.12, TrueRight(360), -0.07, 360, 0.3, 12);
        using var frame = DoughFrame();
        for (var i = 0; i < 6; i++)
        {
            // The jar jumps by 20 px between frames: nothing stable to report.
            using var moved = new Mat(Height, Width, MatType.CV_8UC1, Scalar.All(190));
            var offset = (i % 2) * 20;
            for (var y = 430; y < 700; y++)
                Cv2.Line(moved, new Point((int)TrueLeft(y) + 4 + offset, y),
                    new Point((int)TrueRight(y) - 4 + offset, y), Scalar.All(50));
            estimator.Observe(moved, 430, 700, prior);
        }
        Assert.Null(estimator.Walls);
    }
}
