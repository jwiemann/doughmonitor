using OpenCvSharp;

namespace SourdoughMonitor.Vision;

/// <summary>Measures the jar's tilted glass walls from the dough silhouette: below the dough
/// surface the dough fills the jar to the glass, so on every row the strongest horizontal
/// gradient next to the wall predicted by <see cref="JarWallTracker"/> is the wall. A robust line
/// fit over the rows gives one tilted line per wall for the frame; the median of the recent
/// frames is the model. A model is only reported while the frames agree with each other
/// (<see cref="MaxSpreadPx"/>), so a single glare frame or a moving dough lip cannot move it, and
/// no absolute edge score is needed: flat daylight, where the glass is almost invisible against
/// the wall, still works because the dough edge is not.</summary>
public sealed class JarGeometryEstimator
{
    private const int History = 24;
    private const int MinObservations = 5;
    private const double MaxSpreadPx = 4.0;
    private const int RowStep = 4;
    private const int Window = 45;
    private const double MinEdgeStrength = 10;
    private const double InlierPx = 6.0;
    private const double MaxSlope = 0.25;
    /// <summary>The dough edge sits just inside the glass; the wall line is drawn on the glass.</summary>
    private const double OutwardPx = 3.0;
    /// <summary>The rows (fraction of the frame height) where frames are compared; extrapolating to
    /// the frame edges would multiply the noise of a short line segment.</summary>
    private const double TopRef = 0.25, BottomRef = 0.85;
    /// <summary>How far above the dough surface (fraction of the frame height) wall texture is searched.</summary>
    private const double TextureSpan = 0.45;
    private const double MinTextureEnergy = 2.5;
    private const double TextureRatio = 3.0;

    private readonly Queue<(double LeftTop, double LeftBottom, double RightTop, double RightBottom)> _history = new();
    private int _frameHeight;
    private int _moved;
    /// <summary>A frame this far from the model's median at the top reference row counts as a moved jar.</summary>
    private const double MovedPx = 15;
    private readonly Queue<int> _tops = new();
    /// <summary>Jump of the row mean over six rows that marks the rim.</summary>
    private const double RimJump = 25;

    /// <summary>The model the recent frames agree on, or null while there are too few or
    /// contradicting observations.</summary>
    public JarWalls? Walls { get; private set; }

    /// <summary>The row of the glass rim's upper edge (median of the recent frames), or null.</summary>
    public int? TopRow { get; private set; }

    public void Reset()
    {
        _history.Clear();
        _tops.Clear();
        Walls = null;
        TopRow = null;
    }

    /// <summary>The first row, scanning down from the frame top, where the mean intensity between the
    /// walls jumps (the bright rim ridge against the even wall behind it); null when no row jumps.</summary>
    private static int? GlassTop(Mat gray, Line leftLine, Line rightLine, int surfaceRow)
    {
        var last = Math.Min(surfaceRow, gray.Height - 5);
        var means = new double[last + 1];
        for (var y = 0; y <= last; y++)
        {
            var x0 = (int)Math.Round(leftLine.At(y)) + 25;
            var x1 = (int)Math.Round(rightLine.At(y)) - 25;
            if (x0 < 0 || x1 >= gray.Width || x1 - x0 < 40) return null;
            var sum = 0;
            for (var x = x0; x <= x1; x++) sum += gray.At<byte>(y, x);
            means[y] = sum / (double)(x1 - x0 + 1);
        }
        for (var y = 6; y + 6 <= last; y++)
        {
            if (Math.Abs(means[y + 3] - means[y - 3]) < RimJump) continue;
            var bestY = y;
            for (var k = y; k < Math.Min(y + 8, last - 3); k++)
                if (Math.Abs(means[k + 3] - means[k - 3]) > Math.Abs(means[bestY + 3] - means[bestY - 3])) bestY = k;
            return bestY;
        }
        return null;
    }

    /// <summary>Adds the measurement of one frame. <paramref name="surfaceRow"/> and
    /// <paramref name="floorRow"/> bound the rows where the dough touches both walls; the prior
    /// supplies where to look.</summary>
    public void Observe(Mat gray, int surfaceRow, int floorRow, JarWalls? prior)
    {
        if (prior is null || gray.Empty()) return;
        var height = gray.Height;
        if (height != _frameHeight)
        {
            Reset();
            _frameHeight = height;
        }
        var from = surfaceRow + Math.Max(12, height / 40);
        var to = Math.Min(floorRow, height - 1) - Math.Max(55, height / 12);

        var left = new List<(double Y, double X)>();
        var right = new List<(double Y, double X)>();
        for (var y = from; y <= to; y += RowStep)
        {
            if (EdgeNear(gray, y, prior.LeftAt(y)) is { } l) left.Add((y, l));
            if (EdgeNear(gray, y, prior.RightAt(y)) is { } r) right.Add((y, r));
        }

        // A small dough sample leaves too few rows below the surface to give a slope. The empty
        // glass above it still separates from the wall by texture (see TextureEdgeNear), which
        // extends the line over the whole jar. The points are shifted so the common outward
        // offset below lands them on the glass edge itself.
        var textureFrom = Math.Max(4, surfaceRow - (int)(height * TextureSpan));
        var textureTo = surfaceRow - Math.Max(20, height / 36);
        if (textureTo - textureFrom >= height / 8)
        {
            using var detail = DetailEnergy(gray);
            for (var y = textureFrom; y <= textureTo; y += RowStep)
            {
                if (TextureEdgeNear(detail, y, prior.LeftAt(y), outerIsLeft: true) is { } l) left.Add((y, l + OutwardPx));
                if (TextureEdgeNear(detail, y, prior.RightAt(y), outerIsLeft: false) is { } r) right.Add((y, r - OutwardPx));
            }
        }
        if (left.Count == 0 || right.Count == 0) return;
        var span = Math.Min(left.Max(p => p.Y) - left.Min(p => p.Y), right.Max(p => p.Y) - right.Min(p => p.Y));
        if (span < height / 6.0) return;
        if (FitLine(left) is not { } leftLine || FitLine(right) is not { } rightLine) return;
        if (rightLine.At(height / 2.0) - leftLine.At(height / 2.0) < gray.Width / 12.0) return;

        var observation = (
            LeftTop: leftLine.At(height * TopRef) - OutwardPx, LeftBottom: leftLine.At(height * BottomRef) - OutwardPx,
            RightTop: rightLine.At(height * TopRef) + OutwardPx, RightBottom: rightLine.At(height * BottomRef) + OutwardPx);
        // A moved jar: the recent frames agree with each other but not with the older ones. Three in
        // a row away from the model's median start it afresh instead of waiting hours for the old
        // observations to age out.
        if (_history.Count >= MinObservations)
        {
            var median = _history.Select(h => h.LeftTop).OrderBy(v => v).ElementAt(_history.Count / 2);
            var medianRight = _history.Select(h => h.RightTop).OrderBy(v => v).ElementAt(_history.Count / 2);
            _moved = Math.Abs(observation.LeftTop - median) > MovedPx || Math.Abs(observation.RightTop - medianRight) > MovedPx
                ? _moved + 1 : 0;
            if (_moved >= 3)
            {
                _history.Clear();
                _tops.Clear();
                TopRow = null;
                _moved = 0;
            }
        }
        _history.Enqueue(observation);
        while (_history.Count > History) _history.Dequeue();
        if (GlassTop(gray, leftLine, rightLine, surfaceRow) is { } top)
        {
            _tops.Enqueue(top);
            while (_tops.Count > History) _tops.Dequeue();
            TopRow = _tops.Count >= MinObservations ? _tops.OrderBy(v => v).ElementAt(_tops.Count / 2) : null;
        }
        Walls = Consolidate(height);
    }

    private JarWalls? Consolidate(int height)
    {
        if (_history.Count < MinObservations) return null;
        double[] Column(Func<(double LeftTop, double LeftBottom, double RightTop, double RightBottom), double> pick) =>
            _history.Select(pick).OrderBy(v => v).ToArray();
        var lt = Column(h => h.LeftTop);
        var lb = Column(h => h.LeftBottom);
        var rt = Column(h => h.RightTop);
        var rb = Column(h => h.RightBottom);
        foreach (var values in new[] { lt, lb, rt, rb })
        {
            // Spread of the middle of the distribution: the outermost value on either side may
            // be the odd frame.
            var trim = values.Length >= 8 ? 1 : 0;
            if (values[values.Length - 1 - trim] - values[trim] > MaxSpreadPx * 2) return null;
        }
        double Median(double[] v) => v[v.Length / 2];
        var refY = height / 2.0;
        var span = height * (BottomRef - TopRef);
        var leftSlope = (Median(lb) - Median(lt)) / span;
        var rightSlope = (Median(rb) - Median(rt)) / span;
        return new JarWalls(
            Median(lt) + leftSlope * (refY - height * TopRef), leftSlope,
            Median(rt) + rightSlope * (refY - height * TopRef), rightSlope,
            refY, 1.0, _history.Count);
    }

    /// <summary>The x of the strongest horizontal gradient of the row (averaged over five rows)
    /// within the window around the predicted wall, or null when nothing stands out.</summary>
    private static double? EdgeNear(Mat gray, int y, double predicted)
    {
        var x0 = (int)Math.Round(predicted) - Window;
        var x1 = (int)Math.Round(predicted) + Window;
        if (x0 < 4 || x1 >= gray.Width - 4 || y < 3 || y >= gray.Height - 3) return null;
        var profile = new double[x1 - x0 + 1];
        for (var i = 0; i < profile.Length; i++)
        {
            var sum = 0;
            for (var dy = -2; dy <= 2; dy++) sum += gray.At<byte>(y + dy, x0 + i);
            profile[i] = sum / 5.0;
        }
        var best = 0.0;
        var bestX = -1;
        for (var i = 3; i < profile.Length - 3; i++)
        {
            var g = Math.Abs(profile[i + 3] - profile[i - 3]);
            if (g <= best) continue;
            best = g;
            bestX = i;
        }
        return best >= MinEdgeStrength && bestX >= 0 ? x0 + bestX : null;
    }

    /// <summary>Per-pixel fine-detail energy |frame − blurred frame|: high on a textured wall
    /// (stucco, wallpaper), near zero inside smooth glass and on a lamp's glow.</summary>
    private static Mat DetailEnergy(Mat gray)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 3);
        var detail = new Mat();
        Cv2.Absdiff(gray, blurred, detail);
        return detail;
    }

    /// <summary>The x where, within the window around the predicted wall, the detail energy drops
    /// from the wall side to the glass side, or null when the wall is not clearly textured
    /// against smooth glass (dough in the glass, a smooth wall, a lamp behind the wall).</summary>
    private static double? TextureEdgeNear(Mat detail, int y, double predicted, bool outerIsLeft)
    {
        const int Band = 6, Reach = 24, Gap = 5;
        var x0 = (int)Math.Round(predicted) - Window - Reach - Gap;
        var x1 = (int)Math.Round(predicted) + Window + Reach + Gap;
        if (x0 < 0 || x1 >= detail.Width || y < Band || y >= detail.Height - Band) return null;
        var prefix = new double[x1 - x0 + 2];
        for (var i = 0; i <= x1 - x0; i++)
        {
            var sum = 0;
            for (var dy = -Band; dy <= Band; dy++) sum += detail.At<byte>(y + dy, x0 + i);
            prefix[i + 1] = prefix[i] + sum / (2.0 * Band + 1);
        }
        double Mean(int from, int to) => (prefix[to - x0 + 1] - prefix[from - x0]) / (to - from + 1);
        var best = 0.0;
        var bestX = -1;
        for (var x = (int)Math.Round(predicted) - Window; x <= (int)Math.Round(predicted) + Window; x++)
        {
            var outer = outerIsLeft ? Mean(x - Gap - Reach, x - Gap) : Mean(x + Gap, x + Gap + Reach);
            var inner = outerIsLeft ? Mean(x + Gap, x + Gap + Reach) : Mean(x - Gap - Reach, x - Gap);
            if (outer < MinTextureEnergy || outer < TextureRatio * Math.Max(inner, 0.4)) continue;
            var score = outer - inner;
            if (score <= best) continue;
            best = score;
            bestX = x;
        }
        return bestX >= 0 ? bestX : null;
    }

    private readonly record struct Line(double Intercept, double Slope)
    {
        public double At(double y) => Intercept + Slope * y;
    }

    /// <summary>Theil-Sen line through the edge points, refined over the inliers; null when
    /// too few points agree.</summary>
    private static Line? FitLine(List<(double Y, double X)> points)
    {
        if (points.Count < 10) return null;
        var slopes = new List<double>();
        for (var i = 0; i < points.Count; i++)
        for (var j = i + 1; j < points.Count; j++)
            if (points[j].Y - points[i].Y >= 40)
                slopes.Add((points[j].X - points[i].X) / (points[j].Y - points[i].Y));
        if (slopes.Count == 0) return null;
        slopes.Sort();
        var slope = slopes[slopes.Count / 2];
        var intercepts = points.Select(p => p.X - slope * p.Y).OrderBy(v => v).ToList();
        var line = new Line(intercepts[intercepts.Count / 2], slope);
        var inliers = points.Where(p => Math.Abs(p.X - line.At(p.Y)) <= InlierPx).ToList();
        if (inliers.Count < 10 || inliers.Count < points.Count * 0.5) return null;
        // Least squares over the inliers.
        var my = inliers.Average(p => p.Y);
        var mx = inliers.Average(p => p.X);
        var sxx = inliers.Sum(p => (p.Y - my) * (p.Y - my));
        if (sxx < 1) return null;
        var s = inliers.Sum(p => (p.Y - my) * (p.X - mx)) / sxx;
        if (Math.Abs(s) > MaxSlope) return null;
        return new Line(mx - s * my, s);
    }
}
