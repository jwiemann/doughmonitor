using OpenCvSharp;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Vision;

/// <summary>Locates the container via its vertical walls and the dough surface via a hybrid of
/// dark-band detection (robust for backlit jars: bright glass above, opaque dough band below,
/// possibly bright again underneath) and horizontal edge energy (robust for diffusely lit boxes).
/// Falls back to a full-frame column when walls are not detectable. Fully self-tuning: Canny
/// thresholds derive from frame intensity, wall length from frame size.</summary>
public sealed class JarLevelDetector(VisionOptions options)
{
    /// <summary>Minimum mean-gray-level contrast between the region directly above a dark band
    /// and the band interior for the band to be trusted over the edge-energy method. Without
    /// backlighting, the strongest bright/dark step in the column is often the jar's own base
    /// (glass foot, table-contact shadow) rather than the actual dough surface, and it can still
    /// score noticeably above a low threshold (observed: 50 on a real ambient-lit jar, versus the
    /// "massive" step a true backlit dough band produces). Raised well above that observed false
    /// positive so such frames fall back to the edge-energy method instead of confidently
    /// reporting the jar's base as the dough surface.</summary>
    private const double MinStepContrast = 55.0;

    public DetectionDiagnostics? LastDiagnostics { get; private set; }

    /// <summary>JPEG bytes of the most recently annotated debug image, or null if none yet.</summary>
    public byte[]? LatestAnnotatedImageBytes { get; private set; }

    private sealed record WallLine(int X, int Top, int Bottom);

    private sealed record JarColumn(int Left, int Right, int Top, int Bottom);

    public LevelMeasurement? Measure(byte[] jpegBytes, DateTimeOffset now)
    {
        using var raw = Cv2.ImDecode(jpegBytes, ImreadModes.Grayscale);
        if (raw.Empty()) return null;
        using var img = ApplyConfiguredRoi(raw);
        using var blurred = new Mat();
        Cv2.GaussianBlur(img, blurred, new Size(5, 5), 0);
        // Pass 1: wall-based detection at two Canny relaxation levels.
        foreach (var relaxation in new[] { 1.0, 0.6 })
        {
            using var edges = AutoCanny(blurred, relaxation);
            var jarColumn = FindJarColumn(edges, img.Width, img.Height, relaxation);
            if (jarColumn is null) continue;
            var measurement = MeasureWithinColumn(edges, img, jarColumn, now);
            if (measurement is not null) return measurement;
        }
        // Pass 2: walls invisible (transparent container / box filling the frame)
        // Use the full frame (minus a border margin) as the column.
        foreach (var relaxation in new[] { 1.0, 0.6 })
        {
            using var edges = AutoCanny(blurred, relaxation);
            var fallbackColumn = BuildFallbackColumn(img);
            var measurement = MeasureWithinColumn(edges, img, fallbackColumn, now);
            if (measurement is not null) return measurement;
        }
        if (options.DebugSaveAnnotatedImages)
            SaveDebugImage(img, null, null, now);
        return null;
    }

    private LevelMeasurement? MeasureWithinColumn(Mat edges, Mat gray, JarColumn jarColumn, DateTimeOffset now)
    {
        var inset = Math.Max(3, (jarColumn.Right - jarColumn.Left) / 10);
        var rect = new Rect(
            jarColumn.Left + inset,
            jarColumn.Top,
            jarColumn.Right - jarColumn.Left - 2 * inset,
            jarColumn.Bottom - jarColumn.Top);
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        using var columnEdges = edges[rect];
        using var columnGray = gray[rect];
        var doughTop = FindDoughSurface(columnEdges, columnGray);
        if (doughTop is null)
        {
            SaveDebugImage(gray, jarColumn, null, now);
            return null;
        }
        var jarBottom = FindJarBottom(columnEdges, columnGray, doughTop.Value, rect.Height - 1);
        SaveDebugImage(gray, jarColumn, jarColumn.Top + doughTop.Value, now);
        return new LevelMeasurement(now, jarColumn.Top + doughTop.Value, jarColumn.Top, jarColumn.Top + jarBottom);
    }

    /// <summary>Full-frame column with a small border margin to avoid frame-edge artifacts.
    /// Used when no container walls can be detected.</summary>
    private static JarColumn BuildFallbackColumn(Mat img)
    {
        var marginX = Math.Max(3, img.Width / 20);
        var marginY = Math.Max(2, img.Height / 30);
        return new JarColumn(marginX, img.Width - marginX, marginY, img.Height - marginY);
    }

    private static Mat AutoCanny(Mat blurred, double relaxation)
    {
        var median = ComputeMedianIntensity(blurred);
        var lower = Math.Max(10, 0.66 * median * relaxation);
        var upper = Math.Max(lower + 20, 1.33 * median);
        var edges = new Mat();
        Cv2.Canny(blurred, edges, lower, upper);
        return edges;
    }

    private static double ComputeMedianIntensity(Mat gray)
    {
        var hist = new Mat();
        Cv2.CalcHist([gray], [0], null, hist, 1, [256], [new Rangef(0, 256)]);
        var total = gray.Rows * gray.Cols;
        var half = total / 2.0;
        double cumulative = 0;
        for (var i = 0; i < 256; i++)
        {
            cumulative += hist.At<float>(i);
            if (cumulative >= half) return i;
        }
        return 128;
    }

    private Mat ApplyConfiguredRoi(Mat src)
    {
        if (options is { RoiX: not null, RoiY: not null, RoiWidth: not null, RoiHeight: not null })
        {
            var rect = new Rect(options.RoiX.Value, options.RoiY.Value, options.RoiWidth.Value, options.RoiHeight.Value)
                       & new Rect(0, 0, src.Width, src.Height);
            return src[rect]
                .Clone();
        }
        return src.Clone();
    }

    private JarColumn? FindJarColumn(Mat edges, int frameWidth, int frameHeight, double relaxation)
    {
        var minWallLength = (int)(frameHeight * options.MinJarWallFraction * relaxation);
        var minJarWidth = (int)(frameWidth * options.MinJarWidthFraction);
        var lines = Cv2.HoughLinesP(
            edges,
            1,
            Math.PI / 180,
            threshold: 60,
            minLineLength: minWallLength,
            maxLineGap: 20);
        // Angle-based verticality (~5 degrees) instead of a fixed pixel delta:
        // tapered jars (Weck) produce long, slightly slanted wall lines that a
        // fixed-tolerance filter rejects.
        var verticals = lines.Where(l =>
            {
                var dx = Math.Abs(l.P1.X - l.P2.X);
                var dy = Math.Abs(l.P1.Y - l.P2.Y);
                return dy > 0 && dx <= Math.Max(4, dy * 0.09);
            })
            .Select(l => new WallLine((l.P1.X + l.P2.X) / 2, Math.Min(l.P1.Y, l.P2.Y), Math.Max(l.P1.Y, l.P2.Y)))
            .OrderBy(l => l.X)
            .ToArray();
        if (verticals.Length < 2) return null;
        (WallLine Left, WallLine Right, int Overlap)? best = null;
        foreach (var a in verticals)
        foreach (var b in verticals)
        {
            if (b.X - a.X < minJarWidth) continue;
            var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            if (overlap < minWallLength / 2) continue;
            if (best is null || overlap > best.Value.Overlap)
                best = (a, b, overlap);
        }
        if (best is null) return null;
        var (wl, wr, _) = best.Value;
        return new JarColumn(wl.X, wr.X, Math.Max(wl.Top, wr.Top), Math.Min(wl.Bottom, wr.Bottom));
    }

    private void SaveDebugImage(Mat image, JarColumn? jarColumn, int? doughSurfaceY, DateTimeOffset now)
    {
        using var color = new Mat();
        Cv2.CvtColor(image, color, ColorConversionCodes.GRAY2BGR);
        if (jarColumn is not null)
        {
            Cv2.Line(
                color,
                new Point(jarColumn.Left, jarColumn.Top),
                new Point(jarColumn.Left, jarColumn.Bottom),
                Scalar.Green,
                2);
            Cv2.Line(
                color,
                new Point(jarColumn.Right, jarColumn.Top),
                new Point(jarColumn.Right, jarColumn.Bottom),
                Scalar.Green,
                2);
        }
        if (jarColumn is not null && doughSurfaceY is not null)
            Cv2.Line(
                color,
                new Point(0, doughSurfaceY.Value),
                new Point(color.Width, doughSurfaceY.Value),
                Scalar.Red,
                2);
        Cv2.ImEncode(".jpg", color, out var bytes);
        LatestAnnotatedImageBytes = bytes;
        if (options.DebugSaveAnnotatedImages)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, options.DebugOutputDirectory);
            Directory.CreateDirectory(directory);
            var fileName = $"{now:yyyyMMdd_HHmmssfff}.jpg";
            Cv2.ImWrite(Path.Combine(directory, fileName), color);
        }
    }

    private int? FindDoughSurface(Mat columnEdges, Mat columnGray)
    {
        var rowEnergy = ReduceRows(columnEdges);
        // Intensity profile from the central strip of the column only: the dough's dark
        // center sits mid-column, while glowing glass flanks and room background at the
        // sides would otherwise lift the row means and hide the dough band.
        var stripX = columnGray.Width / 4;
        var stripWidth = Math.Max(1, columnGray.Width / 2);
        using var centralStrip = columnGray[new Rect(stripX, 0, stripWidth, columnGray.Height)];
        // Row median rather than row mean: IR illumination commonly puts a narrow, very
        // bright specular hot spot or condensation glare through the center of the jar. A
        // mean pulls the whole row toward that hot spot even though it covers only a
        // fraction of the row's width, dragging the detected band boundary up into the
        // glare instead of down to the real dough surface. The median ignores it as long as
        // it covers less than half the strip width, which it reliably does.
        var rowIntensity = ReduceRowsMedian(centralStrip);
        var result = FindDoughSurfaceCombined(rowEnergy, rowIntensity, out var diagnostics);
        LastDiagnostics = diagnostics ?? new DetectionDiagnostics("none", 0, null, null);
        return result;
    }

    private static int FindJarBottom(Mat columnEdges, Mat columnGray, int doughTop, int fallbackBottom)
    {
        var stripX = columnGray.Width / 4;
        var stripWidth = Math.Max(1, columnGray.Width / 2);
        using var centralStrip = columnEdges[new Rect(stripX, 0, stripWidth, columnEdges.Height)];
        var rowEnergy = ReduceRows(centralStrip);
        var lowerEdge = FindJarBottomFromHorizontalEdge(centralStrip, doughTop, fallbackBottom);
        if (lowerEdge is not null)
            return lowerEdge.Value;
        return FindJarBottomFromEnergy(rowEnergy, fallbackBottom, doughTop);
    }

    private static int? FindJarBottomFromHorizontalEdge(Mat columnEdges, int doughTop, int fallbackBottom)
    {
        if (columnEdges.Rows < 3 || columnEdges.Cols < 3) return null;
        var lines = Cv2.HoughLinesP(
            columnEdges,
            1,
            Math.PI / 180,
            threshold: 22,
            minLineLength: Math.Max(8, columnEdges.Cols / 4),
            maxLineGap: 8);
        var horizontals = lines.Where(l =>
            {
                var dx = Math.Abs(l.P1.X - l.P2.X);
                var dy = Math.Abs(l.P1.Y - l.P2.Y);
                return dx > 0 && dy <= Math.Max(2, dx * 0.03);
            })
            .Select(l => new
            {
                Y = (l.P1.Y + l.P2.Y) / 2,
                Length = Math.Abs(l.P1.X - l.P2.X)
            })
            .Where(x => x.Y > doughTop + 2 && x.Y <= fallbackBottom)
            .OrderByDescending(x => x.Length)
            .ThenByDescending(x => x.Y)
            .ToArray();
        return horizontals.Length > 0 ? horizontals[0].Y : null;
    }

    public static int FindJarBottomFromEnergy(IReadOnlyList<float> rowEnergy, int fallbackBottom, int? doughTop = null)
    {
        if (rowEnergy.Count == 0) return fallbackBottom;
        var smoothed = MovingAverage(rowEnergy, 5);
        var searchStart = Math.Max(0, (doughTop ?? 0) + 1);
        searchStart = Math.Max(searchStart, (int)(smoothed.Length * 0.35));
        var searchEnd = Math.Max(searchStart + 1, Math.Min(smoothed.Length - 1, fallbackBottom));
        if (searchEnd <= searchStart) return fallbackBottom;
        var window = smoothed.Skip(searchStart)
            .Take(Math.Max(1, searchEnd - searchStart + 1))
            .ToArray();
        if (window.Length == 0) return fallbackBottom;
        var baseline = window.Average();
        var maxEnergy = window.Max();
        if (maxEnergy <= baseline) return fallbackBottom;
        var bestRow = -1;
        var bestScore = double.NegativeInfinity;
        for (var y = searchStart; y <= searchEnd; y++)
        {
            var energy = smoothed[y];
            var left = y > 0 ? smoothed[y - 1] : energy;
            var right = y < smoothed.Length - 1 ? smoothed[y + 1] : energy;
            var prominence = energy - Math.Min(left, right);
            var peakStrength = energy - baseline;
            var depthBias = (double)(y - searchStart) / Math.Max(1, searchEnd - searchStart + 1) * 0.05;
            var lowerBias = (double)(searchEnd - y) / Math.Max(1, searchEnd - searchStart + 1) * 0.15;
            var score = peakStrength * 1.35 + prominence * 0.75 + lowerBias - depthBias;
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = y;
            }
        }
        return bestRow >= 0 ? bestRow : fallbackBottom;
    }

    private static float[] ReduceRows(Mat column)
    {
        var values = new float[column.Rows];
        using var reduced = new Mat();
        Cv2.Reduce(
            column,
            reduced,
            ReduceDimension.Column,
            ReduceTypes.Sum,
            MatType.CV_32F);
        reduced.GetArray(out values);
        return values;
    }

    /// <summary>Per-row median intensity of an 8-bit grayscale Mat. Unlike a mean, a single
    /// bright or dark outlier patch within a row (glare, a condensation droplet) cannot shift
    /// the result as long as it covers less than half the row's width.</summary>
    private static float[] ReduceRowsMedian(Mat column)
    {
        var rows = column.Rows;
        var cols = column.Cols;
        var result = new float[rows];
        var buffer = new byte[cols];
        var mid = cols / 2;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
                buffer[x] = column.Get<byte>(y, x);
            Array.Sort(buffer);
            result[y] = cols % 2 == 0 ? (buffer[mid - 1] + buffer[mid]) / 2f : buffer[mid];
        }
        return result;
    }

    public static LevelMeasurement AdjustMeasurementForRoi(LevelMeasurement measurement, int roiY)
    {
        return new LevelMeasurement(
            measurement.Time,
            measurement.DoughTopPx + roiY,
            measurement.JarTopPx + roiY,
            measurement.JarBottomPx + roiY);
    }

    /// <summary>Hybrid dough surface detection. Opaque dough in a backlit transparent container
    /// forms a dark sustained band in the column intensity profile with a bright region directly
    /// above it (light through glass). The band's top edge is the dough surface. This is far more
    /// reliable than edge energy, which also fires on embossed lettering, glare edges and dried
    /// residue on the glass. When a qualifying band exists, its top wins and is refined to the
    /// nearest edge peak. Otherwise (diffuse lighting, boxes) the edge-energy method is used.</summary>
    public static int? FindDoughSurfaceCombined(IReadOnlyList<float> rowEnergy, IReadOnlyList<float> rowIntensity) =>
        FindDoughSurfaceCombined(rowEnergy, rowIntensity, out _);

    private static int? FindDoughSurfaceCombined(
        IReadOnlyList<float> rowEnergy,
        IReadOnlyList<float> rowIntensity,
        out DetectionDiagnostics? diagnostics)
    {
        var bandTop = FindDoughBandTop(rowIntensity, out var bandContrast);
        if (bandTop is not null && bandContrast >= MinStepContrast)
        {
            // Snap to the strongest horizontal edge within a small window around the band top
            // for pixel precision; keep the band-top row if no edge stands out there.
            var window = Math.Max(3, rowEnergy.Count * 3 / 100);
            var from = Math.Max(0, bandTop.Value - window);
            var to = Math.Min(rowEnergy.Count - 1, bandTop.Value + window);
            var bestRow = bandTop.Value;
            var bestEnergy = 0f;
            for (var y = from; y <= to; y++)
            {
                if (rowEnergy[y] > bestEnergy)
                {
                    bestEnergy = rowEnergy[y];
                    bestRow = y;
                }
            }
            diagnostics = new DetectionDiagnostics("band", bandContrast, bandTop, bestRow);
            return bestRow;
        }
        var edgeRow = FindDoughSurfaceFromEnergy(rowEnergy);
        diagnostics = new DetectionDiagnostics(edgeRow is null ? "none" : "edge", bandContrast, bandTop, edgeRow);
        return edgeRow;
    }

    /// <summary>Finds the top edge of the dough band in the row intensity profile.
    /// All sustained dark runs are enumerated; the dough band is the one with the strongest
    /// contrast between the region directly above it and its own interior — backlit glass
    /// above dough produces a massive bright-to-dark transition, while other dark regions
    /// (shadow strip below the jar base, dark table edge) have dark or dim regions above
    /// them and therefore weak above-contrast. Returns null when no run qualifies; the out
    /// parameter reports the winning run's above-contrast.</summary>
    public static int? FindDoughBandTop(IReadOnlyList<float> rowIntensity, out double contrast)
    {
        contrast = 0;
        if (rowIntensity.Count < 10) return null;
        var smoothed = MovingAverage(rowIntensity, 7);
        var n = smoothed.Length;
        var searchStart = Math.Max(1, (int)(n * 0.05));
        var searchEnd = Math.Min(n - 1, (int)(n * 0.95));
        if (searchEnd - searchStart < 5) return null;
        var prefix = new double[n];
        for (var i = 0; i < n; i++)
        {
            prefix[i] = i == 0 ? smoothed[0] : prefix[i - 1] + smoothed[i];
        }
        var total = prefix[n - 1];
        double minValue = double.PositiveInfinity, maxValue = double.NegativeInfinity;
        for (var y = searchStart; y < searchEnd; y++)
        {
            if (smoothed[y] < minValue) minValue = smoothed[y];
            if (smoothed[y] > maxValue) maxValue = smoothed[y];
        }
        if (maxValue - minValue < MinStepContrast) return null;
        int? bestRow = null;
        var bestScore = double.NegativeInfinity;
        var bestContrast = 0.0;
        var scores = new double[n];
        for (var y = searchStart; y < searchEnd; y++)
        {
            var meanAbove = prefix[y] / y;
            var meanBelow = (total - prefix[y]) / (n - y);
            var stepContrast = meanAbove - meanBelow;
            var left = y > 0 ? smoothed[y - 1] : smoothed[y];
            var right = y < n - 1 ? smoothed[y + 1] : smoothed[y];
            var localDrop = left - right;
            var window = Math.Max(2, n / 20);
            var beforeStart = Math.Max(0, y - window);
            var beforeEnd = Math.Max(beforeStart, y - 1);
            var afterStart = Math.Min(n - 1, y + 1);
            var afterEnd = Math.Min(n - 1, y + window);
            var beforeMean = beforeEnd >= beforeStart ? Average(smoothed, beforeStart, beforeEnd) : smoothed[y];
            var afterMean = afterEnd >= afterStart ? Average(smoothed, afterStart, afterEnd) : smoothed[y];
            var sustainedDrop = beforeMean - afterMean;
            var score = stepContrast * 1.1 + Math.Max(0.0, sustainedDrop) * 0.8 + Math.Max(0.0, localDrop) * 0.25;
            scores[y] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = y;
                bestContrast = stepContrast;
            }
        }
        if (bestRow is null || bestContrast < MinStepContrast) return null;
        var threshold = bestScore * 0.85;
        var selectedRow = bestRow.Value;
        for (var y = searchStart; y < bestRow.Value; y++)
        {
            if (scores[y] >= threshold && bestContrast >= MinStepContrast)
            {
                selectedRow = y;
                break;
            }
        }
        contrast = bestContrast;
        return selectedRow;
    }

    private static double Average(double[] values, int start, int end)
    {
        if (end < start) return values[start];
        var sum = 0.0;
        for (var i = start; i <= end; i++) sum += values[i];
        return sum / (end - start + 1);
    }

    public static int? FindDoughSurfaceFromEnergy(IReadOnlyList<float> rowEnergy)
    {
        if (rowEnergy.Count == 0) return null;
        var smoothed = MovingAverage(rowEnergy, 5);
        var maxEnergy = smoothed.Max();
        if (maxEnergy <= 0) return null;
        var searchStart = Math.Max(0, (int)(smoothed.Length * 0.10));
        var searchEnd = Math.Max(searchStart + 1, (int)(smoothed.Length * 0.90));
        var baseline = smoothed.Skip(searchStart)
            .Take(searchEnd - searchStart)
            .Average();
        var threshold = baseline + (maxEnergy - baseline) * 0.20;
        var bestRow = -1;
        var bestScore = double.NegativeInfinity;
        for (var y = searchStart; y < searchEnd; y++)
        {
            var energy = smoothed[y];
            if (energy < threshold) continue;
            var left = y > 0 ? smoothed[y - 1] : energy;
            var right = y < smoothed.Length - 1 ? smoothed[y + 1] : energy;
            var prominence = energy - Math.Min(left, right);
            var depthBias = (double)(y - searchStart) / Math.Max(1, searchEnd - searchStart);
            var score = (energy - baseline) * 1.2 + prominence * 0.8 + depthBias * 0.25;
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = y;
            }
        }
        if (bestRow < 0)
        {
            for (var y = searchStart; y < searchEnd; y++)
            {
                var energy = smoothed[y];
                var left = y > 0 ? smoothed[y - 1] : energy;
                var right = y < smoothed.Length - 1 ? smoothed[y + 1] : energy;
                var prominence = energy - Math.Min(left, right);
                var depthBias = (double)(y - searchStart) / Math.Max(1, searchEnd - searchStart);
                var score = (energy - baseline) * 0.8 + prominence * 0.4 + depthBias * 0.15;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRow = y;
                }
            }
        }
        return bestRow >= 0 ? bestRow : null;
    }

    private static double[] MovingAverage(IReadOnlyList<float> values, int window)
    {
        var result = new double[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var from = Math.Max(0, i - window / 2);
            var to = Math.Min(values.Count - 1, i + window / 2);
            double sum = 0;
            for (var j = from; j <= to; j++) sum += values[j];
            result[i] = sum / (to - from + 1);
        }
        return result;
    }
}