using System.Text.Json;

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

    /// <summary>Outcome of the most recent Measure call: "detected", "dark_frame",
    /// "decode_failed", "no_jar" or "no_surface". Lets offline replay report why a frame
    /// produced no measurement without re-running detection.</summary>
    public string LastOutcome { get; private set; } = "no_data";

    /// <summary>JPEG bytes of the most recently annotated debug image, or null if none yet.</summary>
    public byte[]? LatestAnnotatedImageBytes { get; private set; }

    private sealed record WallLine(int X, int Top, int Bottom);

    private sealed record JarColumn(int Left, int Right, int Top, int Bottom);

    /// <summary>Measures the dough level. <paramref name="sessionBaselineHeightPx"/>, when
    /// supplied, is the dough height (JarBottomPx - DoughTopPx) recorded at the start of the
    /// current tracked session; it is drawn on the debug image as a reference line so the
    /// starting point stays visible even as the dough rises.</summary>
    public LevelMeasurement? Measure(byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx = null)
    {
        using var raw = Cv2.ImDecode(jpegBytes, ImreadModes.Grayscale);
        if (raw.Empty())
        {
            LastOutcome = "decode_failed";
            LastDiagnostics = new DetectionDiagnostics("decode_failed", 0, null, null);
            return null;
        }
        using var img = ApplyConfiguredRoi(raw);
        // Intensity statistics computed once per frame: Canny thresholds and the
        // dark-frame gate share them.
        var (medianIntensity, p10, p90) = ComputeIntensityStats(img);
        var frameStats = (Mean: (double?)Cv2.Mean(img).Val0, Median: (double?)medianIntensity,
            P10: (double?)p10, P90: (double?)p90);
        // Night frames without backlight are uniformly dark: nothing bright enough to be
        // a lit jar (P90 floor) and no meaningful contrast (P90-P10 floor). The edge-energy
        // method would "detect" noise there and pollute the growth series. Percentile-based
        // so a backlit night frame (dark room, bright jar) still passes.
        if (p90 < options.MinFrameIntensity || p90 - p10 < options.MinFrameContrast)
        {
            LastOutcome = "dark_frame";
            LastDiagnostics = WithFrameStats(new DetectionDiagnostics("dark", 0, null, null), frameStats);
            if (options.DebugSaveAnnotatedImages)
                SaveDebugImage(img, null, null, null, null, now);
            return null;
        }
        using var blurred = new Mat();
        Cv2.GaussianBlur(img, blurred, new Size(5, 5), 0);
        // Pass 1: wall-based detection at two Canny relaxation levels.
        foreach (var relaxation in new[] { 1.0, 0.6 })
        {
            using var edges = AutoCanny(blurred, medianIntensity, relaxation);
            var jarColumn = FindJarColumn(edges, img.Width, img.Height, relaxation);
            if (jarColumn is null) continue;
            var measurement = MeasureWithinColumn(edges, img, jarColumn, now, frameStats, sessionBaselineHeightPx);
            if (measurement is not null)
            {
                LastOutcome = "detected";
                return measurement;
            }
        }
        // Pass 2: walls invisible (transparent container / box filling the frame)
        // Use the full frame (minus a border margin) as the column.
        foreach (var relaxation in new[] { 1.0, 0.6 })
        {
            using var edges = AutoCanny(blurred, medianIntensity, relaxation);
            var fallbackColumn = BuildFallbackColumn(img);
            var measurement = MeasureWithinColumn(edges, img, fallbackColumn, now, frameStats, sessionBaselineHeightPx);
            if (measurement is not null)
            {
                LastOutcome = "detected";
                return measurement;
            }
        }
        LastOutcome = "no_surface";
        LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
        if (options.DebugSaveAnnotatedImages)
            SaveDebugImage(img, null, null, null, null, now);
        return null;
    }

    private LevelMeasurement? MeasureWithinColumn(
        Mat edges,
        Mat gray,
        JarColumn jarColumn,
        DateTimeOffset now,
        (double? Mean, double? Median, double? P10, double? P90) frameStats,
        double? sessionBaselineHeightPx)
    {
        var inset = Math.Max(3, (jarColumn.Right - jarColumn.Left) / 10);
        var rect = new Rect(
            jarColumn.Left + inset,
            jarColumn.Top,
            jarColumn.Right - jarColumn.Left - 2 * inset,
            jarColumn.Bottom - jarColumn.Top);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
            return null;
        }
        using var columnEdges = edges[rect];
        using var columnGray = gray[rect];
        var doughTop = FindDoughSurface(columnEdges, columnGray, frameStats);
        if (doughTop is null)
        {
            SaveDebugImage(gray, jarColumn, null, null, null, now);
            return null;
        }
        var jarBottom = FindJarBottom(columnEdges, columnGray, doughTop.Value, rect.Height - 1);
        // The jar's physical bottom doesn't move between frames, so the session-start dough
        // surface can be re-derived in this frame's coordinates from this frame's jar bottom.
        var sessionStartSurfaceY = sessionBaselineHeightPx is not null
            ? (int?)Math.Round(jarColumn.Top + jarBottom - sessionBaselineHeightPx.Value)
            : null;
        SaveDebugImage(
            gray,
            jarColumn,
            jarColumn.Top + doughTop.Value,
            jarColumn.Top + jarBottom,
            sessionStartSurfaceY,
            now);
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

    private static Mat AutoCanny(Mat blurred, double medianIntensity, double relaxation)
    {
        var lower = Math.Max(10, 0.66 * medianIntensity * relaxation);
        var upper = Math.Max(lower + 20, 1.33 * medianIntensity);
        var edges = new Mat();
        Cv2.Canny(blurred, edges, lower, upper);
        return edges;
    }

    private static (double Median, double P10, double P90) ComputeIntensityStats(Mat gray)
    {
        var hist = new Mat();
        Cv2.CalcHist([gray], [0], null, hist, 1, [256], [new Rangef(0, 256)]);
        var total = gray.Rows * gray.Cols;
        double Percentile(double fraction)
        {
            var target = total * fraction;
            var running = 0.0;
            for (var i = 0; i < 256; i++)
            {
                running += hist.At<float>(i);
                if (running >= target) return i;
            }
            return 255;
        }
        var median = 255.0;
        var half = total / 2.0;
        var cumulative = 0.0;
        for (var i = 0; i < 256; i++)
        {
            cumulative += hist.At<float>(i);
            if (cumulative >= half)
            {
                median = i;
                break;
            }
        }
        return (median, Percentile(0.10), Percentile(0.90));
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
        // Outer (union) span of the wall pair: Hough segment lengths vary with lighting,
        // and intersecting them would make the column — and with it every height
        // measurement — wobble frame to frame.
        return new JarColumn(wl.X, wr.X, Math.Min(wl.Top, wr.Top), Math.Max(wl.Bottom, wr.Bottom));
    }

    private static readonly Scalar SessionStartLineColor = new(0, 255, 255); // yellow (BGR)

    /// <summary>Path of the debug image written for the most recent Measure call, or null
    /// when disk persistence is off (annotated bytes only) or the frame failed to decode.</summary>
    public string? LastDebugImagePath { get; private set; }

    private void SaveDebugImage(
        Mat image,
        JarColumn? jarColumn,
        int? doughSurfaceY,
        int? jarBottomY,
        int? sessionStartSurfaceY,
        DateTimeOffset now)
    {
        LastDebugImagePath = null;
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
        if (sessionStartSurfaceY is not null)
        {
            var y = Math.Clamp(sessionStartSurfaceY.Value, 0, color.Height - 1);
            Cv2.Line(color, new Point(0, y), new Point(color.Width, y), SessionStartLineColor, 1, LineTypes.Link8);
            Cv2.PutText(
                color,
                "session start",
                new Point(4, Math.Clamp(y - 6, 10, color.Height - 4)),
                HersheyFonts.HersheySimplex,
                0.4,
                SessionStartLineColor,
                1);
        }
        Cv2.ImEncode(".jpg", color, out var bytes);
        LatestAnnotatedImageBytes = bytes;
        if (!options.DebugSaveAnnotatedImages) return;
        var directory = ResolveDebugDirectory();
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{now:yyyyMMdd_HHmmssfff}.jpg");
        // Timestamps from file-mtime fallbacks can collide (same second or FAT mtime
        // granularity); never overwrite a sibling debug image.
        for (var index = 1; File.Exists(filePath); index++)
        {
            filePath = Path.Combine(directory, $"{now:yyyyMMdd_HHmmssfff}_{index}.jpg");
        }
        Cv2.ImWrite(filePath, color);
        LastDebugImagePath = filePath;
        AppendDiagnosticsLogEntry(directory, Path.GetFileName(filePath), jarColumn, doughSurfaceY, jarBottomY, sessionStartSurfaceY, now);
        PruneOldDebugFiles(directory, now);
    }

    private string ResolveDebugDirectory() =>
        Path.IsPathFullyQualified(options.DebugOutputDirectory)
            ? options.DebugOutputDirectory
            : Path.Combine(AppContext.BaseDirectory, options.DebugOutputDirectory);

    /// <summary>Appends one JSON line per frame to a daily-rotated sidecar log next to the
    /// annotated images, so an exported debug folder carries the raw numbers (detection
    /// method, band contrast, pixel positions) alongside what the images show, instead of
    /// requiring MQTT debug mode to have been captured separately.</summary>
    private void AppendDiagnosticsLogEntry(
        string directory,
        string imageFileName,
        JarColumn? jarColumn,
        int? doughSurfaceY,
        int? jarBottomY,
        int? sessionStartSurfaceY,
        DateTimeOffset now)
    {
        var entry = new
        {
            time = now.ToString("O"),
            image = imageFileName,
            method = LastDiagnostics?.Method,
            band_contrast = LastDiagnostics?.BandContrast,
            band_top_row = LastDiagnostics?.BandTopRow,
            final_row = LastDiagnostics?.FinalRow,
            jar_left_px = jarColumn?.Left,
            jar_right_px = jarColumn?.Right,
            jar_top_px = jarColumn?.Top,
            dough_top_px = doughSurfaceY,
            jar_bottom_px = jarBottomY,
            session_start_surface_px = sessionStartSurfaceY
        };
        var logPath = Path.Combine(directory, $"diagnostics-{now:yyyyMMdd}.jsonl");
        File.AppendAllText(logPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    /// <summary>Deletes debug images and diagnostics log files older than
    /// <see cref="VisionOptions.DebugRetentionHours"/> so the export folder stays a bounded,
    /// recent-only rolling window instead of accumulating one file per sample forever.</summary>
    private void PruneOldDebugFiles(string directory, DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime - TimeSpan.FromHours(options.DebugRetentionHours);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a file locked by a concurrent reader (e.g. the
                    // user copying it out) can simply be retried on the next cycle.
                }
            }
        }
        catch (IOException)
        {

        }
    }

    private int? FindDoughSurface(
        Mat columnEdges,
        Mat columnGray,
        (double? Mean, double? Median, double? P10, double? P90) frameStats)
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
        LastDiagnostics = WithFrameStats(diagnostics ?? new DetectionDiagnostics("none", 0, null, null), frameStats);
        return result;
    }

    private static DetectionDiagnostics WithFrameStats(
        DetectionDiagnostics diagnostics,
        (double? Mean, double? Median, double? P10, double? P90) frameStats) =>
        diagnostics with
        {
            FrameMean = frameStats.Mean,
            FrameMedian = frameStats.Median,
            FrameP10 = frameStats.P10,
            FrameP90 = frameStats.P90
        };

    /// <summary>Resolves the jar bottom in column coordinates. The wall-derived column lower
    /// bound is the default; a visible horizontal edge only refines it within a band just
    /// above that bound. Picking any strong edge below the dough (e.g. the dough surface
    /// edge itself) collapses the height and poisons the rise series.</summary>
    private static int FindJarBottom(Mat columnEdges, Mat columnGray, int doughTop, int fallbackBottom)
    {
        var bandStart = Math.Max(doughTop + 2, fallbackBottom - Math.Max(4, fallbackBottom / 4));
        var lowerEdge = FindJarBottomFromHorizontalEdge(columnEdges, bandStart, fallbackBottom);
        return lowerEdge ?? fallbackBottom;
    }

    private static int? FindJarBottomFromHorizontalEdge(Mat columnEdges, int bandStart, int fallbackBottom)
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
            .Where(x => x.Y >= bandStart && x.Y <= fallbackBottom)
            .OrderByDescending(x => x.Length)
            .ThenByDescending(x => x.Y)
            .ToArray();
        return horizontals.Length > 0 ? horizontals[0].Y : null;
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