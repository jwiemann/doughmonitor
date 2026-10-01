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

    /// <summary>Recent warm-extent column bounds. The dough's visible extent flickers with
    /// lighting (edges wash out; occasional warm structures beside the jar pollute). The
    /// INTERSECTION over the last <see cref="WarmColumnHistorySize"/> frames (~1 h) is the
    /// region that was dough in every recent frame: static drawn lines, a measurement
    /// strip guaranteed inside the dough, immune to both washout and pollution, and
    /// self-healing after a bumped camera once the polluted window ages out.</summary>
    private readonly Queue<(int Left, int Right)> _recentWarmColumns = new();

    private const int WarmColumnHistorySize = 60;

    /// <summary>Consecutive frames whose fresh warm extent disagreed with the window
    /// aggregate on both edges — three in a row mean the camera/scene moved and the
    /// window must re-establish on the new scene instead of blending two scenes for an
    /// hour.</summary>
    private int _extentDisagreementStreak;

    /// <summary>Pixel size of the previous frame. A change (camera reconfiguration) makes
    /// all stored extents invalid instantly — bounds from a 1280px frame index out of
    /// range on a 640px one.</summary>
    private (int Width, int Height)? _lastFrameSize;

    /// <summary>Raised when the scene-change detector fires (jar geometry moved). The host
    /// resets the analyzer session here: the rise baseline is a pixel height measured in
    /// the old geometry and is meaningless after a camera move.</summary>
    public event Action? SceneChanged;

    private (int Left, int Right)? _persistedGeometry = LoadGeometry(options.GeometryStateFilePath);

    private bool _geometrySeeded;

    private static (int Left, int Right)? LoadGeometry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var filePath = ResolveGeometryPath(path);
            if (!File.Exists(filePath)) return null;
            var node = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(filePath));
            return (node.GetProperty("left").GetInt32(), node.GetProperty("right").GetInt32());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    private void PersistGeometry(int left, int right)
    {
        if (string.IsNullOrWhiteSpace(options.GeometryStateFilePath)) return;
        try
        {
            var filePath = ResolveGeometryPath(options.GeometryStateFilePath);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(filePath, JsonSerializer.Serialize(new { left, right }));
            _persistedGeometry = (left, right);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: without persistence, camera moves across restarts just need the
            // manual reset button.
        }
    }

    private static string ResolveGeometryPath(string path) =>
        Path.IsPathFullyQualified(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

    private sealed record WallLine(int X, int Top, int Bottom);

    /// <summary>How the jar column was established: "walls" (Hough-detected vertical glass
    /// walls), "warm" (the dough's warm horizontal extent — those edges are the glass) or
    /// "fallback" (full-frame minus border margin; drawn orange so unverified bounds stay
    /// visually distinct from verified walls).</summary>
    private sealed record JarColumn(int Left, int Right, int Top, int Bottom, string Kind);

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
        // Camera reconfiguration (resolution change) invalidates every stored extent
        // instantly; the scene-change streak below cannot save the in-between frames.
        var frameSize = (img.Width, img.Height);
        if (_lastFrameSize is { } last && last != frameSize)
        {
            _recentWarmColumns.Clear();
            _extentDisagreementStreak = 0;
        }
        _lastFrameSize = frameSize;
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
                SaveDebugImage(img, null, null, null, null, now, null);
            return null;
        }
        using var blurred = new Mat();
        Cv2.GaussianBlur(img, blurred, new Size(5, 5), 0);
        // Visualization mask for the debug image: where the color filter (warm tone) sees
        // dough. Off by default (DebugHighlightDough) — the translucent overlay makes it
        // hard to tell shadows from actual dough when inspecting the raw scene.
        using var visWarm = options.DebugHighlightDough ? ComputeVisWarmMask(imgColor, img) : null;
        // Pass 1: wall-based detection at two Canny relaxation levels.
        foreach (var relaxation in new[] { 1.0, 0.6 })
        {
            using var edges = AutoCanny(blurred, medianIntensity, relaxation);
            var jarColumn = FindJarColumn(edges, img.Width, img.Height, relaxation);
            if (jarColumn is null) continue;
            var measurement = MeasureWithinColumn(edges, img, imgColor, jarColumn, now, frameStats, sessionBaselineHeightPx, visWarm);
            if (measurement is not null)
            {
                LastOutcome = "detected";
                return measurement;
            }
        }
        // Pass 2: saturation-derived walls — the dough's warm horizontal extent marks the
        // jar interior, so its edges ARE the glass walls. Robust where the glass has no
        // Canny contrast (ambient light against a white wall). The returned bounds are the
        // median over the recent window, so the drawn lines sit still.
        {
            using var edges = AutoCanny(blurred, medianIntensity, 1.0);
            var warmColumn = FindJarColumnByWarmExtent(
                imgColor,
                img,
                img.Width,
                img.Height,
                options.WarmColumnSaturationStep,
                options.WarmColumnMinFraction);
            if (warmColumn is not null)
            {
                var measurement = MeasureWithinColumn(edges, img, imgColor, warmColumn, now, frameStats, sessionBaselineHeightPx, visWarm);
                if (measurement is not null)
                {
                    LastOutcome = "detected";
                    return measurement;
                }
                // The held window no longer contains the dough (bumped camera): drop it so
                // the next frame re-establishes on fresh extents.
                _recentWarmColumns.Clear();
                _extentDisagreementStreak = 0;
            }
        }
        // Pass 3: walls invisible (transparent container / box filling the frame)
        // Use the full frame (minus a border margin) as the column.
        foreach (var relaxation in new[] { 1.0, 0.6 })
        {
            using var edges = AutoCanny(blurred, medianIntensity, relaxation);
            var fallbackColumn = BuildFallbackColumn(img);
            var measurement = MeasureWithinColumn(edges, img, imgColor, fallbackColumn, now, frameStats, sessionBaselineHeightPx, visWarm);
            if (measurement is not null)
            {
                LastOutcome = "detected";
                return measurement;
            }
        }
        LastOutcome = "no_surface";
        LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
        if (options.DebugSaveAnnotatedImages)
            SaveDebugImage(img, null, null, null, null, now, visWarm);
        return null;
    }

    private LevelMeasurement? MeasureWithinColumn(
        Mat edges,
        Mat gray,
        Mat color,
        JarColumn jarColumn,
        DateTimeOffset now,
        (double? Mean, double? Median, double? P10, double? P90) frameStats,
        double? sessionBaselineHeightPx,
        Mat? visWarm)
    {
        var inset = Math.Max(3, (jarColumn.Right - jarColumn.Left) / 10);
        var rect = new Rect(
            jarColumn.Left + inset,
            jarColumn.Top,
            jarColumn.Right - jarColumn.Left - 2 * inset,
            jarColumn.Bottom - jarColumn.Top);
        // Defensive clamp: stale/held columns (scene change, seeding) must never index
        // out of the current frame.
        rect &= new Rect(0, 0, gray.Width, gray.Height);
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
            SaveDebugImage(gray, jarColumn, null, null, null, now, visWarm);
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
            now,
            visWarm);
        return new LevelMeasurement(now, jarColumn.Top + doughTop.Value, jarColumn.Top, jarColumn.Top + jarBottom);
    }

    /// <summary>Full-frame column with a small border margin to avoid frame-edge artifacts.
    /// Used when no container walls can be detected.</summary>
    private static JarColumn BuildFallbackColumn(Mat img)
    {
        var marginX = Math.Max(3, img.Width / 20);
        var marginY = Math.Max(2, img.Height / 30);
        return new JarColumn(marginX, img.Width - marginX, marginY, img.Height - marginY, "fallback");
    }

    /// <summary>Derives the jar column from the dough's horizontal extent, using BOTH dough
    /// signals: warm tone (mature tan dough) OR darkness relative to the glass above (a
    /// fresh-fed pale slurry has almost no saturation but is clearly darker than the empty
    /// glass). A column belongs to the jar when a large fraction of its dough-band rows is
    /// dough; the extreme columns of the longest warm/dark run are the glass walls. Warm
    /// structures beside the jar (a cream-colored rack) are excluded by the neutral wall
    /// gap; the dark door frame by the hard border-column exclusion. Note
    /// <see cref="ReduceDimension.Row"/> semantics: reduce to a single ROW = per-column
    /// statistic (the inverse choice produced row indices masquerading as column
    /// bounds).</summary>
    private JarColumn? FindJarColumnByWarmExtent(
        Mat color,
        Mat gray,
        int frameWidth,
        int frameHeight,
        double warmColumnSaturationStep,
        double warmColumnMinFraction)
    {
        // Seed once from the persisted column so a camera move made while the add-on was
        // down is caught by the same disagreement logic as a live bump. The seed ages out
        // of the window after WarmColumnHistorySize frames.
        if (!_geometrySeeded)
        {
            _geometrySeeded = true;
            if (_persistedGeometry is { } seededColumn) _recentWarmColumns.Enqueue(seededColumn);
        }
        ComputeTopQuarterRefs(color, gray, out var satNeutral, out var grayBright);
        using var saturation = SaturationMap(color);
        if (saturation.Empty()) return null;

        // Dough mask = warm OR dark, then the dough's row band: the longest run of rows whose
        // dough coverage is at least half the width. A fixed zone would dilute the column
        // fractions whenever the dough sits low (fresh feed): most zone rows are empty
        // glass, and a column's fraction drops below acceptance even though it is solidly
        // dough across the actual band.
        using var warmMask = new Mat();
        Cv2.Threshold(saturation, warmMask, satNeutral + warmColumnSaturationStep, 255, ThresholdTypes.Binary);
        using var darkMask = new Mat();
        Cv2.Threshold(gray, darkMask, grayBright - 18.0, 255, ThresholdTypes.BinaryInv);
        using var doughMask = new Mat();
        Cv2.Max(warmMask, darkMask, doughMask);
        var rowCoverage = CoverageProfile(doughMask);
        var bandTop = -1;
        var bandBottom = -1;
        var bandStart = -1;
        var bestBand = 0;
        var bandSearchStart = (int)(frameHeight * 0.35);
        var bandSearchEnd = Math.Min(frameHeight - 1, (int)(frameHeight * 0.95));
        for (var y = bandSearchStart; y <= bandSearchEnd + 1; y++)
        {
            var on = y <= bandSearchEnd && rowCoverage[y] >= 0.5;
            if (on && bandStart < 0) bandStart = y;
            if ((!on || y > bandSearchEnd) && bandStart >= 0)
            {
                if (y - bandStart > bestBand)
                {
                    bestBand = y - bandStart;
                    bandTop = bandStart;
                    bandBottom = y - 1;
                }
                bandStart = -1;
            }
        }
        if (bandTop < 0 || bestBand < frameHeight * 0.05) return null;
        var minJarWidth = (int)(frameWidth * 0.04);
        var bandRect = new Rect(0, bandTop, frameWidth, bandBottom - bandTop + 1);
        int left;
        int right;
        // Warm-first: the color signal is the most specific dough evidence — everything
        // else in the scene is neutral white. A warm run wide enough to be the jar
        // interior defines the column exactly, INCLUDING the right edge where the dark
        // signal bridges onto shadowed wall/rack (observed: extent right drifting to the
        // door frame at 1203 while the dough ends at ~800). The dark signal only fills in
        // when the dough is too pale for the color filter (fresh feed).
        using var warmBand = warmMask[bandRect];
        using var warmColMeans = new Mat();
        Cv2.Reduce(warmBand, warmColMeans, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
        if (warmColMeans.GetArray(out float[] warmMeans) && warmMeans.Length == frameWidth)
        {
            var warmColumns = SmoothColumns(warmMeans, warmColumnMinFraction, frameWidth);
            (left, right, var warmLength) = LongestRun(warmColumns);
            if (warmLength < Math.Max(minJarWidth, (int)(frameWidth * 0.25)))
            {
                // Pale dough: fall back to warm ∪ dark. The dark extent is shadow-prone on
                // the right, but the pale phase is temporary and the surface strip survives
                // the wall minority via the row medians.
                using var bandMask = doughMask[bandRect];
                using var zoneColMeans = new Mat();
                Cv2.Reduce(bandMask, zoneColMeans, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
                if (!zoneColMeans.GetArray(out float[] zoneMeans) || zoneMeans.Length != frameWidth) return null;
                var doughColumns = SmoothColumns(zoneMeans, warmColumnMinFraction, frameWidth);
                (left, right, var doughLength) = LongestRun(doughColumns);
                if (doughLength < minJarWidth) return null;
            }
        }
        else
        {
            return null;
        }
        _recentWarmColumns.Enqueue((left, right));
        while (_recentWarmColumns.Count > WarmColumnHistorySize) _recentWarmColumns.Dequeue();
        // Densest-cluster mode per edge (±3 px): good-light frames agree on the dough edge,
        // washout frames scatter to narrower extents, occasional pollution scatters wider —
        // the mode sits at the true edge and stays put while good frames dominate the
        // window; a lasting regime change migrates the mode smoothly.
        var columnLeft = DensestCluster(_recentWarmColumns.Select(c => c.Left), 3);
        var columnRight = DensestCluster(_recentWarmColumns.Select(c => c.Right), 3);
        if (columnRight - columnLeft < minJarWidth) return null;
        var marginY = Math.Max(2, frameHeight / 30);
        // Scene-change detector: both edges far off the aggregate means the camera moved.
        // After three consecutive such frames, restart the window on the new scene —
        // otherwise old-scene bounds would pollute measurements for a full window length.
        if (Math.Abs(left - columnLeft) > frameWidth * 0.15
            && Math.Abs(right - columnRight) > frameWidth * 0.15)
        {
            _extentDisagreementStreak++;
            if (_extentDisagreementStreak >= 3)
            {
                _recentWarmColumns.Clear();
                _recentWarmColumns.Enqueue((left, right));
                _extentDisagreementStreak = 0;
                PersistGeometry(left, right);
                // The rise baseline is a pixel height in the OLD geometry; hosts must
                // reset the analyzer session when this fires.
                SceneChanged?.Invoke();
                return new JarColumn(left, right, marginY, frameHeight - marginY, "warm");
            }
        }
        else
        {
            _extentDisagreementStreak = 0;
        }
        if (_persistedGeometry is not { } persisted
            || persisted.Left != columnLeft || persisted.Right != columnRight)
        {
            PersistGeometry(columnLeft, columnRight);
        }
        return new JarColumn(columnLeft, columnRight, marginY, frameHeight - marginY, "warm");
    }

    /// <summary>Column on/off decisions from a per-column mean profile: warm/dark enough, with
    /// border columns hard-excluded (the door frames sit at x≈64/1216 and are dark at every
    /// row, so a neighbor rule would keep them in the run) and isolated warm speckles
    /// suppressed via the neighbor rule.</summary>
    private static bool[] SmoothColumns(float[] means, double fractionThreshold, int frameWidth)
    {
        var line = fractionThreshold * 255f;
        var result = new bool[means.Length];
        for (var x = 0; x < means.Length; x++)
        {
            if (x < frameWidth * 0.06 || x > frameWidth * 0.94) continue;
            if (means[x] < line) continue;
            var leftNeighbor = x > 0 && means[x - 1] >= line;
            var rightNeighbor = x < means.Length - 1 && means[x + 1] >= line;
            result[x] = leftNeighbor || rightNeighbor;
        }
        return result;
    }

    /// <summary>Longest contiguous run of true columns, not leftmost/rightmost overall:
    /// warm-looking structures beside the jar (observed: a cream-colored metal rack) are
    /// separated from the dough by a neutral wall gap, and taking the outermost warm
    /// columns would jump the extent onto them.</summary>
    private static (int Left, int Right, int Length) LongestRun(bool[] columns)
    {
        var left = -1;
        var right = -1;
        var runStart = -1;
        var best = 0;
        for (var x = 0; x <= columns.Length; x++)
        {
            var on = x < columns.Length && columns[x];
            if (on && runStart < 0) runStart = x;
            if (!on && runStart >= 0)
            {
                if (x - runStart > best)
                {
                    best = x - runStart;
                    left = runStart;
                    right = x - 1;
                }
                runStart = -1;
            }
        }
        return (left, right, best);
    }

    /// <summary>Center of the densest ±<paramref name="tolerance"/> cluster of the given
    /// values (the modal edge position of recent extents). Averaging the cluster members
    /// damps the quantization to individual pixel values.</summary>
    private static int DensestCluster(IEnumerable<int> values, int tolerance)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var bestStart = 0;
        var bestCount = 0;
        var bestSum = 0;
        var start = 0;
        for (var end = 0; end < sorted.Length; end++)
        {
            while (sorted[end] - sorted[start] > 2 * tolerance) start++;
            var count = end - start + 1;
            if (count > bestCount)
            {
                bestCount = count;
                bestStart = start;
                bestSum = 0;
                for (var i = start; i <= end; i++) bestSum += sorted[i];
            }
        }
        return bestCount > 0 ? (bestSum + bestCount / 2) / bestCount : 0;
    }

    /// <summary>The color filter's dough mask for the debug image: warm-tone pixels above
    /// the neutral top-quarter reference plus <see cref="VisionOptions.MinWarmSaturationStep"/>.</summary>
    private Mat? ComputeVisWarmMask(Mat color, Mat gray)
    {
        ComputeTopQuarterRefs(color, gray, out var satNeutral, out var _);
        var mask = new Mat();
        using var satMap = SaturationMap(color);
        Cv2.Threshold(satMap, mask, satNeutral + options.MinWarmSaturationStep, 255, ThresholdTypes.Binary);
        return mask;
    }

    /// <summary>Neutral references from the frame's top quarter (wall, door frame, empty
    /// glass — bright and colorless): the medians across columns of the per-column mean
    /// saturation and gray intensity. Shared by the column-extent derivation and the
    /// debug visualization masks.</summary>
    private static void ComputeTopQuarterRefs(Mat color, Mat gray, out double satNeutral, out double grayBright)
    {
        var neutralTop = Math.Max(1, (int)(color.Height * 0.25));
        using var satMap = SaturationMap(color);
        using var topSat = satMap[new Rect(0, 0, color.Width, neutralTop)];
        using var redSat = new Mat();
        Cv2.Reduce(topSat, redSat, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
        redSat.GetArray(out float[] satMeans);
        satNeutral = Median(satMeans);
        using var topGray = gray[new Rect(0, 0, gray.Width, neutralTop)];
        using var redGray = new Mat();
        Cv2.Reduce(topGray, redGray, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
        redGray.GetArray(out float[] grayMeans);
        grayBright = Median(grayMeans);
    }

    private static double Median(float[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2f
            : sorted[sorted.Length / 2];
    }

    /// <summary>BGR channel spread (max − min) as a single-channel saturation proxy,
    /// computed with native OpenCV ops.</summary>
    private static Mat SaturationMap(Mat color)
    {
        Cv2.Split(color, out var channels);
        try
        {
            var max = new Mat();
            Cv2.Max(channels[0], channels[1], max);
            Cv2.Max(max, channels[2], max);
            var min = new Mat();
            Cv2.Min(channels[0], channels[1], min);
            Cv2.Min(min, channels[2], min);
            var spread = new Mat();
            Cv2.Subtract(max, min, spread);
            return spread;
        }
        finally
        {
            foreach (var channel in channels) channel.Dispose();
        }
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
        return new JarColumn(wl.X, wr.X, Math.Min(wl.Top, wr.Top), Math.Max(wl.Bottom, wr.Bottom), "walls");
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
        DateTimeOffset now,
        Mat? visWarm)
    {
        LastDebugImagePath = null;
        using var color = new Mat();
        Cv2.CvtColor(image, color, ColorConversionCodes.GRAY2BGR);
        // Show what the color filter sees: orange = warm-tone pixels (the dough). Without
        // this the debug image is a bare grayscale frame and the color-based decisions
        // are invisible.
        if (visWarm is not null)
        {
            using var original = color.Clone();
            using var overlay = color.Clone();
            overlay.SetTo(new Scalar(0, 165, 255), visWarm);
            Cv2.AddWeighted(original, 0.55, overlay, 0.45, 0, color);
        }
        if (jarColumn is not null)
        {
            // Green = verified jar bounds (Hough walls or warm extent). Orange = fallback
            // full-frame bounds with no verified walls, so an unverified frame is
            // recognizable at a glance.
            var wallColor = jarColumn.Kind == "fallback" ? new Scalar(0, 165, 255) : Scalar.Green;
            var thickness = jarColumn.Kind == "fallback" ? 1 : 2;
            Cv2.Line(
                color,
                new Point(jarColumn.Left, jarColumn.Top),
                new Point(jarColumn.Left, jarColumn.Bottom),
                wallColor,
                thickness);
            Cv2.Line(
                color,
                new Point(jarColumn.Right, jarColumn.Top),
                new Point(jarColumn.Right, jarColumn.Bottom),
                wallColor,
                thickness);
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
            jar_column_kind = jarColumn?.Kind,
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
        // Coverage profiles: per-row fraction of strip pixels that are dough (warm / dark).
        // The row MEDIAN flips as soon as the dough's back edge (which appears higher
        // through the cylindrical glass) covers half the strip — the front edge, where the
        // dough reaches the front glass, is only visible as a coverage step. Computed over
        // the jar interior (see InteriorBounds) so wall columns don't cap the fraction.
        float[]? warmCoverage = null;
        float[]? darkCoverage = null;
        ComputeTopQuarterRefs(centralStripColor, centralStrip, out var satNeutral, out var _);
        if (satNeutral is { } satRef)
        {
            using var satMap = SaturationMap(centralStripColor);
            using var warmMask = new Mat();
            Cv2.Threshold(satMap, warmMask, satRef + options.MinWarmSaturationStep, 255, ThresholdTypes.Binary);
            warmCoverage = CoverageProfile(warmMask);
            if (InteriorBounds(warmMask, warmCoverage) is { } warmInterior)
            {
                warmCoverage = CoverageProfile(warmMask[new Rect(warmInterior.Left, 0, warmInterior.Right - warmInterior.Left + 1, warmMask.Rows)]);
            }
        }
        var brightRef = BrightReferenceLevel(MovingAverage(rowIntensity, 7));
        if (brightRef is { } bright)
        {
            using var darkMask = new Mat();
            Cv2.Threshold(centralStrip, darkMask, bright - options.MinAmbientBandContrast, 255, ThresholdTypes.BinaryInv);
            darkCoverage = CoverageProfile(darkMask);
            if (InteriorBounds(darkMask, darkCoverage) is { } darkInterior)
            {
                darkCoverage = CoverageProfile(darkMask[new Rect(darkInterior.Left, 0, darkInterior.Right - darkInterior.Left + 1, darkMask.Rows)]);
            }
        }
        var result = FindDoughSurfaceCombined(
            rowEnergy,
            rowIntensity,
            warmCoverage,
            darkCoverage,
            out var diagnostics,
            options.StrongBandContrast,
            options.MinAmbientBandContrast,
            options.DarkBandMaxIntensity,
            options.MinDarkBandFraction,
            options.FrontEdgeCoverageFraction);
        LastDiagnostics = WithFrameStats(diagnostics ?? new DetectionDiagnostics("none", 0, null, null), frameStats);
        return result;
    }

    /// <summary>Per-row fraction (0..1) of mask pixels, via a native average reduction of
    /// the 0/255 mask. ReduceDimension.Column reduces to a single column = one value per
    /// row (the Row direction would yield per-column values).</summary>
    private static float[] CoverageProfile(Mat mask)
    {
        using var reduced = new Mat();
        Cv2.Reduce(mask, reduced, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
        reduced.GetArray(out float[] values);
        for (var i = 0; i < values.Length; i++) values[i] /= 255f;
        return values;
    }

    /// <summary>Jar-interior column bounds from a dough mask: the row with the highest
    /// coverage is a dough-body row, and there the dough spans the full jar width — its
    /// longest mask run is the interior. Used to recompute coverage without the neutral
    /// wall columns of a fallback column, which would cap the front-edge fraction.</summary>
    private static (int Left, int Right)? InteriorBounds(Mat mask, float[] coverage)
    {
        var rows = mask.Rows;
        var cols = mask.Cols;
        var searchStart = (int)(rows * 0.40);
        var searchEnd = Math.Min(rows - 2, (int)(rows * 0.95));
        if (searchEnd - searchStart < 5 || cols < 10) return null;
        var bestRow = -1;
        var bestCoverage = 0f;
        for (var y = searchStart; y <= searchEnd; y++)
        {
            if (coverage[y] > bestCoverage)
            {
                bestCoverage = coverage[y];
                bestRow = y;
            }
        }
        if (bestRow < 0 || bestCoverage < 0.3f) return null;
        var left = -1;
        var right = -1;
        var runStart = -1;
        var bestRun = 0;
        for (var x = 0; x <= cols; x++)
        {
            var on = x < cols && mask.Get<byte>(bestRow, x) > 0;
            if (on && runStart < 0) runStart = x;
            if (!on && runStart >= 0)
            {
                if (x - runStart > bestRun)
                {
                    bestRun = x - runStart;
                    left = runStart;
                    right = x - 1;
                }
                runStart = -1;
            }
        }
        if (left < 0 || bestRun < cols * 0.3) return null;
        return (left, right);
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
        float[]? warmCoverage,
        float[]? darkCoverage,
        out DetectionDiagnostics? diagnostics,
        double strongContrast,
        double minAmbientContrast,
        double darkBandMaxIntensity,
        double minDarkBandFraction,
        double frontEdgeCoverageFraction)
    {
        var longRun = (int)Math.Ceiling(Math.Max(4, rowEnergy.Count * minDarkBandFraction));
        var bandTop = FindDoughBandTop(
            rowIntensity,
            out var bandContrast,
            strongContrast,
            minAmbientContrast,
            darkBandMaxIntensity,
            minDarkBandFraction);
        // The warm (color) boundary IS the surface: the pale stir-smear/frost band above
        // the dough body is static residue on the glass — the dark boundary tracks it and
        // would freeze the level while the dough rises past it. The warm crossing = the
        // colored body's top = the true level. No colored body (fresh pale feed): fall
        // through to the dark band, which then reads the smear boundary — biased high,
        // but present, and re-baselined when the color returns.
        var warmFront = FrontEdgeByWarmCoverage(warmCoverage, longRun);
        if (warmFront is not null)
        {
            var snapped = SnapToEdge(rowEnergy, warmFront.Value);
            var bodyContrast = MeanCoverage(warmCoverage, warmFront.Value, Math.Max(8, longRun / 2));
            diagnostics = new DetectionDiagnostics("warm", bodyContrast, warmFront, snapped);
            return snapped;
        }
        // Qualification (contrast + darkness + length rules) lives in FindDoughBandTop;
        // a non-null result is already accepted.
        if (bandTop is not null)
        {
            var front = RefineToFrontEdge(darkCoverage, bandTop.Value, longRun, frontEdgeCoverageFraction)
                ?? bandTop.Value;
            var snapped = SnapToEdge(rowEnergy, front);
            diagnostics = new DetectionDiagnostics("band", bandContrast, bandTop, snapped);
            return snapped;
        }
        // Edge fallback: the strongest edge row must sit on actual dough — a dark/warm
        // coverage floor rejects condensation-line or glare edges high above the dough
        // (observed live: the droplet boundary at row 377 while the dough starts at 470).
        var edgeRow = FindDoughSurfaceFromEnergy(rowEnergy);
        var edgeSupported = edgeRow is not null
            && MeanCoverage(darkCoverage, edgeRow.Value, Math.Max(8, longRun / 4)) >= 0.4;
        diagnostics = new DetectionDiagnostics(
            edgeSupported ? "edge" : "none",
            bandContrast,
            bandTop,
            edgeSupported ? edgeRow : null);
        return edgeSupported ? edgeRow : null;
    }

    /// <summary>Front edge from the warm-coverage profile: the top of the LONGEST run of rows
    /// whose warm coverage reaches 60% of the plateau (the saturated dough body). The pale
    /// stir-smudge above the dough has warm patches, but they never form a sustained run —
    /// the body does. Returns null when the frame has no colored dough body (plateau below
    /// 50% — pale fresh feed), letting the dark-band path take over.</summary>
    private static int? FrontEdgeByWarmCoverage(float[]? coverage, int longRun)
    {
        if (coverage is null || coverage.Length == 0) return null;
        var n = coverage.Length;
        var searchStart = Math.Min(n - 1, (int)(n * 0.05));
        var plateau = 0f;
        for (var y = searchStart; y < n; y++)
        {
            if (coverage[y] > plateau) plateau = coverage[y];
        }
        if (plateau < 0.5f) return null;
        var threshold = 0.6 * plateau;
        var minSolid = Math.Max(4, longRun / 2);
        var left = -1;
        var right = -1;
        var runStart = -1;
        var best = 0;
        for (var y = searchStart; y <= n; y++)
        {
            var on = y < n && coverage[y] >= threshold;
            if (on && runStart < 0) runStart = y;
            if (!on && runStart >= 0)
            {
                if (y - runStart > best)
                {
                    best = y - runStart;
                    left = runStart;
                    right = y - 1;
                }
                runStart = -1;
            }
        }
        if (left < 0 || best < minSolid) return null;
        return left;
    }

    /// <summary>Mean coverage over [start .. start+length) (clamped to the profile). A warm
    /// band top only counts as dough when the body BELOW it is also warm — a thin warm
    /// glare line over a pale slurry has no warm body and must not win over the band
    /// method.</summary>
    private static double MeanCoverage(float[]? coverage, int start, int length)
    {
        if (coverage is null || coverage.Length == 0) return 1.0;
        var from = Math.Max(0, Math.Min(start, coverage.Length - 1));
        var to = Math.Min(coverage.Length, from + Math.Max(1, length));
        double sum = 0;
        for (var y = from; y < to; y++) sum += coverage[y];
        return sum / (to - from);
    }

    /// <summary>Refines a back-edge band top down to the FRONT edge: the first row from
    /// <paramref name="bandTop"/> onward where at least
    /// <paramref name="frontEdgeCoverageFraction"/> of the jar-interior width is dough,
    /// sustained for the dough-body length (dips from bubbles/droplets tolerated down to
    /// 60 % of the threshold). Returns null when no coverage profile is available or no
    /// sustained crossing exists — the caller keeps the back-edge top then.</summary>
    private static int? RefineToFrontEdge(
        float[]? coverage,
        int bandTop,
        int longRun,
        double frontEdgeCoverageFraction)
    {
        if (coverage is null || coverage.Length == 0) return null;
        var n = coverage.Length;
        var cont = 0.6 * frontEdgeCoverageFraction;
        for (var y = Math.Min(bandTop, n - 1); y < n; y++)
        {
            if (coverage[y] < frontEdgeCoverageFraction) continue;
            var sustained = true;
            for (var k = 1; k < longRun && y + k < n; k++)
            {
                if (coverage[y + k] < cont) { sustained = false; break; }
            }
            if (sustained) return y;
        }
        return null;
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