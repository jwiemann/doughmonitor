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
        // Color decode: the dough is warm-toned (tan) while glass/wall/background are
        // neutral — the saturation step is the most stable surface signal under ambient
        // light (a fresh-fed light dough shows almost no brightness step at all).
        using var rawColor = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (rawColor.Empty())
        {
            LastOutcome = "decode_failed";
            LastDiagnostics = new DetectionDiagnostics("decode_failed", 0, null, null);
            return null;
        }
        using var rawGray = new Mat();
        Cv2.CvtColor(rawColor, rawGray, ColorConversionCodes.BGR2GRAY);
        using var img = ApplyConfiguredRoi(rawGray);
        using var imgColor = ApplyConfiguredRoi(rawColor);
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
            var measurement = MeasureWithinColumn(edges, img, imgColor, jarColumn, now, frameStats, sessionBaselineHeightPx);
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
            var measurement = MeasureWithinColumn(edges, img, imgColor, fallbackColumn, now, frameStats, sessionBaselineHeightPx);
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
        Mat color,
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
        using var columnColor = color[rect];
        var doughTop = FindDoughSurface(columnEdges, columnGray, columnColor, frameStats);
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
            // Lines hugging the frame border are almost always image-edge artifacts (door
            // frames, vignetting), not jar walls: observed at x≈64/1280 and x≈1216/1280 on a
            // real frame while the actual glass walls had too little contrast for Hough.
            // Rejecting them lets the full-frame fallback column take over, which profiles
            // the jar interior correctly.
            .Where(l => l.X > frameWidth * 0.06 && l.X < frameWidth * 0.94)
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
        Mat columnColor,
        (double? Mean, double? Median, double? P10, double? P90) frameStats)
    {
        var rowEnergy = ReduceRows(columnEdges);
        // Intensity profile from the central strip of the column only: the dough's dark
        // center sits mid-column, while glowing glass flanks and room background at the
        // sides would otherwise lift the row means and hide the dough band.
        var stripX = columnGray.Width / 4;
        var stripWidth = Math.Max(1, columnGray.Width / 2);
        var stripRect = new Rect(stripX, 0, stripWidth, columnGray.Height);
        using var centralStrip = columnGray[stripRect];
        using var centralStripColor = columnColor[stripRect];
        // Row median rather than row mean: IR illumination commonly puts a narrow, very
        // bright specular hot spot or condensation glare through the center of the jar. A
        // mean pulls the whole row toward that hot spot even though it covers only a
        // fraction of the row's width, dragging the detected band boundary up into the
        // glare instead of down to the real dough surface. The median ignores it as long as
        // it covers less than half the strip width, which it reliably does.
        var rowIntensity = ReduceRowsMedian(centralStrip);
        var rowSaturation = ReduceRowsSaturationMedian(centralStripColor);
        var result = FindDoughSurfaceCombined(
            rowEnergy,
            rowIntensity,
            rowSaturation,
            out var diagnostics,
            options.StrongBandContrast,
            options.MinAmbientBandContrast,
            options.DarkBandMaxIntensity,
            options.MinDarkBandFraction,
            options.MinWarmSaturationStep,
            options.StrongWarmSaturation,
            options.MaxNeutralReferenceSaturation);
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

    /// <summary>Hybrid dough surface detection, in order of evidence strength:
    /// 1. "warm" — the saturation step (dough is warm-toned, glass/wall neutral). Most
    ///    stable under ambient light, including a fresh-fed light dough that shows almost
    ///    no brightness step. Validated on real frames: glass sat 2-5, dough sat 25-32.
    /// 2. "band" — the dark-band brightness step relative to the bright glass above
    ///    (strong backlit step, or genuinely dark long band).
    /// 3. "edge" — horizontal edge energy fallback (diffuse-lit boxes).
    /// Every accepted band top is snapped to the strongest horizontal edge within a small
    /// window for pixel precision when one stands out.</summary>
    private static int? FindDoughSurfaceCombined(
        IReadOnlyList<float> rowEnergy,
        IReadOnlyList<float> rowIntensity,
        IReadOnlyList<float> rowSaturation,
        out DetectionDiagnostics? diagnostics,
        double strongContrast,
        double minAmbientContrast,
        double darkBandMaxIntensity,
        double minDarkBandFraction,
        double minWarmSaturationStep,
        double strongWarmSaturation,
        double maxNeutralReferenceSaturation)
    {
        var warmTop = FindWarmBandTop(
            rowSaturation,
            out var warmContrast,
            minWarmSaturationStep,
            strongWarmSaturation,
            maxNeutralReferenceSaturation,
            minDarkBandFraction);
        if (warmTop is not null)
        {
            var snapped = SnapToEdge(rowEnergy, warmTop.Value);
            diagnostics = new DetectionDiagnostics("warm", warmContrast, warmTop, snapped);
            return snapped;
        }
        var bandTop = FindDoughBandTop(
            rowIntensity,
            out var bandContrast,
            strongContrast,
            minAmbientContrast,
            darkBandMaxIntensity,
            minDarkBandFraction);
        // Qualification (contrast + darkness + length rules) lives in FindDoughBandTop;
        // a non-null result is already accepted.
        if (bandTop is not null)
        {
            var snapped = SnapToEdge(rowEnergy, bandTop.Value);
            diagnostics = new DetectionDiagnostics("band", bandContrast, bandTop, snapped);
            return snapped;
        }
        var edgeRow = FindDoughSurfaceFromEnergy(rowEnergy);
        diagnostics = new DetectionDiagnostics(edgeRow is null ? "none" : "edge", bandContrast, bandTop, edgeRow);
        return edgeRow;
    }

    /// <summary>Snaps a band top to the strongest horizontal edge within a small window
    /// around it for pixel precision; returns the band top itself when no edge stands out.</summary>
    private static int SnapToEdge(IReadOnlyList<float> rowEnergy, int bandTop)
    {
        var window = Math.Max(3, rowEnergy.Count * 3 / 100);
        var from = Math.Max(0, bandTop - window);
        var to = Math.Min(rowEnergy.Count - 1, bandTop + window);
        var bestRow = bandTop;
        var bestEnergy = 0f;
        for (var y = from; y <= to; y++)
        {
            if (rowEnergy[y] > bestEnergy)
            {
                bestEnergy = rowEnergy[y];
                bestRow = y;
            }
        }
        return bestRow;
    }

    /// <summary>Finds the dough surface via the warm-tone step: sourdough is tan (the
    /// red/blue channel spread is large) while glass, wall and background are neutral.
    /// The reference level is the modal saturation of the profile's top quarter (the glass
    /// above the dough). The surface is the topmost row whose saturation rises
    /// persistently above (neutral + step) — a long warm band qualifies at the weaker
    /// step, a short one only at the strong step (real dough measured: sat 25-32 vs glass
    /// 2-5). Inverted geometry compared to the dark-band search: here we look for warm
    /// runs, and the neutral dip below the jar bottom breaks the run naturally.</summary>
    public static int? FindWarmBandTop(
        IReadOnlyList<float> rowSaturation,
        out double contrast,
        double minWarmStep = 12.0,
        double strongWarmStep = 22.0,
        double maxNeutralReferenceSaturation = 10.0,
        double minWarmBandFraction = 0.2)
    {
        contrast = 0;
        if (rowSaturation.Count < 10) return null;
        var smoothed = MovingAverage(rowSaturation, 7);
        var n = smoothed.Length;
        var searchStart = Math.Max(1, (int)(n * 0.05));
        if (searchStart >= n - 5) return null;
        var neutral = NeutralReferenceLevel(smoothed);
        if (neutral is null || neutral > maxNeutralReferenceSaturation) return null;
        var line = neutral.Value + minWarmStep;
        var minRun = Math.Max(3, n / 60);
        var longRun = (int)Math.Ceiling(Math.Max(4, n * minWarmBandFraction));
        var insideWindow = Math.Max(4, n / 5);
        for (var y = searchStart; y < n; y++)
        {
            if (smoothed[y] < line) continue;
            var end = y + 1;
            while (end < n && smoothed[end] >= line) end++;
            var runLength = end - y;
            if (runLength < minRun) continue;
            var insideEnd = Math.Min(end, y + insideWindow);
            double insideSum = 0;
            for (var row = y; row < insideEnd; row++) insideSum += smoothed[row];
            var insideMean = insideSum / (insideEnd - y);
            var step = insideMean - neutral.Value;
            if (runLength < longRun && step < strongWarmStep) continue;
            contrast = step;
            return y;
        }
        return null;
    }

    /// <summary>The neutral reference: modal saturation of the profile's top quarter (the
    /// glass/wall above the dough). Returns null when the top quarter holds no dominant
    /// level.</summary>
    private static double? NeutralReferenceLevel(double[] smoothed)
    {
        var quarter = Math.Max(4, smoothed.Length / 4);
        var hist = new int[256];
        for (var y = 0; y < quarter && y < smoothed.Length; y++)
        {
            hist[Math.Clamp((int)Math.Round(smoothed[y]), 0, 255)]++;
        }
        var best = 0;
        var bestCount = 0;
        for (var i = 0; i < 256; i++)
        {
            if (hist[i] > bestCount)
            {
                bestCount = hist[i];
                best = i;
            }
        }
        return bestCount > 0 ? (double?)best : null;
    }

    /// <summary>Per-row median channel spread (max−min across B/G/R) of a BGR strip —
    /// the saturation proxy. Median for the same glare-robustness reasons as the
    /// intensity profile.</summary>
    private static float[] ReduceRowsSaturationMedian(Mat column)
    {
        var rows = column.Rows;
        var cols = column.Cols;
        var result = new float[rows];
        var buffer = new int[cols];
        var mid = cols / 2;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var pixel = column.Get<Vec3b>(y, x);
                var b = pixel.Item0;
                var g = pixel.Item1;
                var r = pixel.Item2;
                var max = Math.Max(b, Math.Max(g, r));
                var min = Math.Min(b, Math.Min(g, r));
                buffer[x] = max - min;
            }
            Array.Sort(buffer);
            result[y] = cols % 2 == 0 ? (buffer[mid - 1] + buffer[mid]) / 2f : buffer[mid];
        }
        return result;
    }

    /// <summary>Finds the top edge of the dough band in the row intensity profile,
    /// relative to the bright reference level above it (the mode of the profile's top
    /// quarter: wall and glass). The surface is the first row that persistently drops
    /// below (brightLevel − MinAmbientBandContrast) and whose interior is genuinely dark.
    /// A long dark band qualifies at the weaker ambient contrast (the real morning scene:
    /// dough only ~20 gray levels below the wall, fading over tens of rows — no Otsu split
    /// and no global above/below step isolates it); a short band must show the massive
    /// backlit step instead. This rejects the jar-base shadow: it is not dark enough
    /// (observed ~150 gray) to pass the darkness gate and its step (~50) is below the
    /// backlit threshold.</summary>
    public static int? FindDoughBandTop(
        IReadOnlyList<float> rowIntensity,
        out double contrast,
        double strongContrast = 55.0,
        double minAmbientContrast = 18.0,
        double darkBandMaxIntensity = 135.0,
        double minDarkBandFraction = 0.2)
    {
        contrast = 0;
        if (rowIntensity.Count < 10) return null;
        var smoothed = MovingAverage(rowIntensity, 7);
        var n = smoothed.Length;
        var searchStart = Math.Max(1, (int)(n * 0.05));
        if (searchStart >= n - 5) return null;
        var brightLevel = BrightReferenceLevel(smoothed);
        if (brightLevel is null) return null;
        var line = brightLevel.Value - minAmbientContrast;
        // Reject single-row dips (lettering, thin residue) but keep short backlit bands.
        var minRun = Math.Max(3, n / 60);
        var longRun = (int)Math.Ceiling(Math.Max(4, n * minDarkBandFraction));
        var insideWindow = Math.Max(4, n / 5);
        for (var y = searchStart; y < n; y++)
        {
            if (smoothed[y] >= line) continue;
            var end = y + 1;
            while (end < n && smoothed[end] < line) end++;
            var runLength = end - y;
            if (runLength < minRun) continue;
            var insideEnd = Math.Min(end, y + insideWindow);
            double insideSum = 0;
            for (var row = y; row < insideEnd; row++) insideSum += smoothed[row];
            var insideMean = insideSum / (insideEnd - y);
            var bandContrast = brightLevel.Value - insideMean;
            if (insideMean > darkBandMaxIntensity && bandContrast < strongContrast) continue;
            if (runLength < longRun && bandContrast < strongContrast) continue;
            contrast = bandContrast;
            return y;
        }
        return null;
    }

    /// <summary>The bright reference level above the dough: the modal value of the
    /// profile's top quarter (wall and glass rows). Rounded histogram mode; null when the
    /// top quarter is implausibly dark (frame mostly covered by something else).</summary>
    private static double? BrightReferenceLevel(double[] smoothed)
    {
        var quarter = Math.Max(4, smoothed.Length / 4);
        var hist = new int[256];
        for (var y = 0; y < quarter && y < smoothed.Length; y++)
        {
            hist[Math.Clamp((int)Math.Round(smoothed[y]), 0, 255)]++;
        }
        var best = 0;
        var bestCount = 0;
        for (var i = 0; i < 256; i++)
        {
            if (hist[i] > bestCount)
            {
                bestCount = hist[i];
                best = i;
            }
        }
        // The wall/glass above the dough must actually be bright for this method to
        // apply; otherwise the frame shows something we don't understand.
        return bestCount > 0 && best >= 100 ? (double?)best : null;
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