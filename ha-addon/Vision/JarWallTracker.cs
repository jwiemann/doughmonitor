using OpenCvSharp;

namespace SourdoughMonitor.Vision;

/// <summary>The glass walls of the jar as two straight lines <c>x = X + Slope * (y - RefY)</c>
/// in frame pixels. The slopes carry the jar's angle: the walls are not vertical (taper and
/// perspective, about 0.1 px per row on this camera), so the jar is about 60 px wider at the top
/// of the frame than at the dough.</summary>
public sealed record JarWalls(
    double LeftX, double LeftSlope, double RightX, double RightSlope, double RefY, double Score, int Frames)
{
    public double LeftAt(double y) => LeftX + LeftSlope * (y - RefY);

    public double RightAt(double y) => RightX + RightSlope * (y - RefY);
}

/// <summary>Finds the jar walls from the persistent vertical edges of the recent frames.
/// Every non-dark frame contributes a Radon-style transform of its normalised horizontal
/// gradient (response of every near-vertical line by position and slope). The low quantile over
/// the last frames keeps only lines present in most of them: the glass walls stay while dough,
/// bubbles and moving reflections drop out. The best left/right pair with a plausible width is the
/// jar; when a clearly weaker outer line sits beyond a strong inner one (a specular streak just
/// inside the wall) the outer line wins. Strong, even lighting (an LED behind the jar) gives a
/// clear model; flat daylight gives a weak one, which the caller must not trust.</summary>
public sealed class JarWallTracker
{
    private const int WorkWidth = 640;
    private const float SlopeMin = -0.12f;
    private const float SlopeStep = 0.01f;
    private const int SlopeCount = 52;
    private const int BufferFrames = 24;
    private const int MinFrames = 12;
    private const float Quantile = 0.3f;
    private const double MinWidthFraction = 190.0 / 640;
    private const double MaxWidthFraction = 360.0 / 640;
    private const double OuterPeakFraction = 0.5;

    // Walls taper inwards towards the bottom: left slope positive, right slope negative.
    private const float LeftSlopeLow = 0.05f, LeftSlopeHigh = 0.19f;
    private const float RightSlopeLow = -0.15f, RightSlopeHigh = -0.05f;

    private readonly Queue<float[]> _radons = new();
    private readonly double _minScore;
    private int _workHeight;

    public JarWallTracker(double minScore) => _minScore = minScore;

    /// <summary>The last model whose score reached the confidence threshold. Kept across weak
    /// (daylight) windows and replaced only by another confident one, so a night's clear
    /// geometry also serves the next day. Weaker fits can sit on reflections or the shelf and
    /// are never reported.</summary>
    public JarWalls? Confident { get; private set; }

    public void Reset()
    {
        _radons.Clear();
        Confident = null;
    }

    /// <summary>Adds the frame to the window and refits.</summary>
    public void Update(Mat gray)
    {
        if (gray.Empty()) return;
        var workHeight = Math.Max(8, (int)Math.Round(gray.Height * (double)WorkWidth / gray.Width));
        if (workHeight != _workHeight)
        {
            _radons.Clear();
            _workHeight = workHeight;
        }
        _radons.Enqueue(ComputeRadon(gray, workHeight));
        while (_radons.Count > BufferFrames) _radons.Dequeue();
        if (_radons.Count < MinFrames) return;
        var fit = Fit(QuantileOf(_radons), gray.Width / (double)WorkWidth, workHeight);
        if (fit is { } walls && walls.Score >= _minScore) Confident = walls with { Frames = _radons.Count };
    }

    private static float[] ComputeRadon(Mat gray, int workHeight)
    {
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(WorkWidth, workHeight), 0, 0, InterpolationFlags.Area);
        using var blurred = new Mat();
        Cv2.GaussianBlur(small, blurred, new Size(0, 0), 1.6);
        using var smoothF = new Mat();
        blurred.ConvertTo(smoothF, MatType.CV_32F);
        using var gradient = new Mat();
        Cv2.Sobel(smoothF, gradient, MatType.CV_32F, 1, 0, 3);
        using var magnitude = new Mat();
        Cv2.Absdiff(gradient, Scalar.All(0), magnitude);
        using var local = new Mat();
        Cv2.Blur(smoothF, local, new Size(31, 31));
        using var denominator = new Mat();
        Cv2.Add(local, Scalar.All(12), denominator);
        using var edges = new Mat();
        Cv2.Divide(magnitude, denominator, edges);

        var top = (int)(workHeight / 6.0);
        var bottom = (int)(workHeight * 11 / 12.0);
        using var band = edges[new Rect(0, top, WorkWidth, bottom - top)].Clone();
        var referenceRow = (workHeight * 190.0 / 360.0) - top;
        var radon = new float[SlopeCount * WorkWidth];
        using var sheared = new Mat();
        using var column = new Mat();
        for (var i = 0; i < SlopeCount; i++)
        {
            var slope = SlopeMin + i * SlopeStep;
            using var transform = new Mat(2, 3, MatType.CV_64F);
            transform.Set(0, 0, 1.0);
            transform.Set(0, 1, (double)slope);
            transform.Set(0, 2, -slope * referenceRow);
            transform.Set(1, 0, 0.0);
            transform.Set(1, 1, 1.0);
            transform.Set(1, 2, 0.0);
            Cv2.WarpAffine(band, sheared, transform, band.Size(),
                InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Constant, Scalar.All(0));
            Cv2.Reduce(sheared, column, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
            column.GetArray(out float[] values);
            Array.Copy(values, 0, radon, i * WorkWidth, WorkWidth);
        }
        return radon;
    }

    private static float[] QuantileOf(IEnumerable<float[]> radons)
    {
        var stack = radons.ToArray();
        var result = new float[stack[0].Length];
        var values = new float[stack.Length];
        var rank = (int)Math.Floor((stack.Length - 1) * Quantile);
        for (var cell = 0; cell < result.Length; cell++)
        {
            for (var i = 0; i < stack.Length; i++) values[i] = stack[i][cell];
            Array.Sort(values);
            result[cell] = values[rank];
        }
        return result;
    }

    private static JarWalls? Fit(float[] radon, double scale, int workHeight)
    {
        var (left, leftSlopes) = BestPerColumn(radon, LeftSlopeLow, LeftSlopeHigh);
        var (right, rightSlopes) = BestPerColumn(radon, RightSlopeLow, RightSlopeHigh);
        var minWidth = (int)(WorkWidth * MinWidthFraction);
        var maxWidth = (int)(WorkWidth * MaxWidthFraction);

        var bestScore = double.MinValue;
        var bestLeft = -1;
        var bestRight = -1;
        for (var xl = 10; xl < WorkWidth - minWidth; xl++)
        {
            var from = xl + minWidth;
            var to = Math.Min(WorkWidth - 6, xl + maxWidth);
            if (to <= from) continue;
            var xr = from;
            for (var x = from; x < to; x++) if (right[x] > right[xr]) xr = x;
            var score = left[xl] + right[xr];
            if (score <= bestScore) continue;
            bestScore = score;
            bestLeft = xl;
            bestRight = xr;
        }
        if (bestLeft < 0) return null;

        for (var pass = 0; pass < 2; pass++)
        {
            var outerLeft = OuterPeak(left, 2, bestRight - minWidth, bestLeft, towardsLower: true);
            if (outerLeft >= 0 && bestRight - outerLeft <= maxWidth) bestLeft = outerLeft;
            var outerRight = OuterPeak(right, bestRight + 1, WorkWidth - 3, bestRight, towardsLower: false);
            if (outerRight >= 0 && outerRight - bestLeft <= maxWidth) bestRight = outerRight;
        }

        var referenceY = workHeight * 190.0 / 360.0 * scale;
        return new JarWalls(
            bestLeft * scale, leftSlopes[bestLeft], bestRight * scale, rightSlopes[bestRight],
            referenceY, left[bestLeft] + right[bestRight], 0);
    }

    /// <summary>The leftmost (or rightmost) local maximum in the range that reaches at least
    /// <see cref="OuterPeakFraction"/> of the current peak and of the profile's maximum, or -1.</summary>
    private static int OuterPeak(float[] profile, int from, int to, int current, bool towardsLower)
    {
        var maximum = 0f;
        foreach (var value in profile) maximum = Math.Max(maximum, value);
        var found = -1;
        for (var x = Math.Max(2, from); x < Math.Min(profile.Length - 2, to + 1); x++)
        {
            if (profile[x] < profile[x - 1] || profile[x] < profile[x + 1]) continue;
            if (profile[x] < OuterPeakFraction * profile[current] || profile[x] < OuterPeakFraction * maximum) continue;
            if (towardsLower ? x >= current : x <= current) continue;
            if (found < 0 || (towardsLower ? x < found : x > found)) found = x;
        }
        return found;
    }

    /// <summary>For every column the strongest response over the slopes in the range, smoothed
    /// along x, and the slope that produced it.</summary>
    private static (float[] Best, float[] Slope) BestPerColumn(float[] radon, float slopeLow, float slopeHigh)
    {
        var best = new float[WorkWidth];
        var slope = new float[WorkWidth];
        for (var i = 0; i < SlopeCount; i++)
        {
            var s = SlopeMin + i * SlopeStep;
            if (s < slopeLow - 1e-4f || s > slopeHigh + 1e-4f) continue;
            for (var x = 0; x < WorkWidth; x++)
            {
                var value = radon[i * WorkWidth + x];
                if (value <= best[x]) continue;
                best[x] = value;
                slope[x] = s;
            }
        }
        // Gaussian sigma 1.5 along x, radius 5.
        var kernel = new float[11];
        var sum = 0f;
        for (var k = -5; k <= 5; k++)
        {
            kernel[k + 5] = (float)Math.Exp(-k * k / (2 * 1.5 * 1.5));
            sum += kernel[k + 5];
        }
        var smoothed = new float[WorkWidth];
        for (var x = 0; x < WorkWidth; x++)
        {
            var acc = 0f;
            for (var k = -5; k <= 5; k++)
                acc += kernel[k + 5] * best[Math.Clamp(x + k, 0, WorkWidth - 1)];
            smoothed[x] = acc / sum;
        }
        return (smoothed, slope);
    }
}
