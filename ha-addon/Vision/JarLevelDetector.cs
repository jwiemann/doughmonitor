using System.Text.Json;

using OpenCvSharp;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Vision;

/// <summary>Locates a measurement column from a backlit glass foot or the dough's
/// ambient-light extent, then traces the lower-connected content boundary. The drawn
/// curve and reported surface share one basis. Geometry is held across lighting
/// changes and invalidated when the camera's coordinate system changes.</summary>
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

    /// <summary>Recent supported column bounds. A densest-cluster mode rejects exposure
    /// washout and one-sided background pollution; confirmed two-sided moves reset
    /// the column and the growth baseline.</summary>
    private readonly Queue<(int Left, int Right)> _recentWarmColumns = new();

    /// <summary>Warm-crossing anchors smoothed over nine samples for exposure flicker.
    /// These position the luminance search window; they are not reported as levels.</summary>
    private readonly Queue<int> _recentWarmFronts = new();

    private const int WarmColumnHistorySize = 12;

    /// <summary>Validated replacement extents awaiting three consistent measurements.</summary>
    private readonly Queue<(int Left, int Right)> _pendingWarmMove = new();

    private JarColumn? _proposedColumn;
    private (int Left, int Right)? _freshColumnExtent;
    private int? _freshWarmFront;
    private bool _resetWarmFrontOnAccept;
    private bool _unverifiedExtent;
    private double? _referenceJarBottomPx;

    /// <summary>Glass-base-to-dough-floor offsets (px) measured in the current scene, newest last.
    /// Their median stands in for a frame whose warm body is too faint to show the floor; the
    /// queue is seeded from the persisted geometry and cleared with every scene change.</summary>
    private readonly Queue<double> _floorOffsets = new();

    private const int FloorHistorySize = 9;

    /// <summary>Offset samples needed before their median is trusted as the scene's offset.</summary>
    private const int FloorMinSamples = 3;

    /// <summary>Floor row measured by the latest successful column measurement, or null when
    /// that frame showed no measurable warm body.</summary>
    private double? _lastMeasuredFloorRow;

    private int _warmColumnSupport;

    /// <summary>Whether the latest column attempt confirmed a visible physical base.</summary>
    private bool _lastColumnBottomConfirmed;

    /// <summary>Persisted image size shared by detection, rendering and growth measurements.
    /// Only differently sized inputs allocate a resize; normal frames stay untouched.</summary>
    private (int Width, int Height)? _referenceFrameSize;

    /// <summary>Raised when the scene-change detector fires (jar geometry moved). The host
    /// resets the analyzer session here: the rise baseline is a pixel height measured in
    /// the old geometry and is meaningless after a camera move.</summary>
    public event Action? SceneChanged;

    private (int Left, int Right, int FrameWidth, int FrameHeight, double? Bottom, double? FloorOffset)? _persistedGeometry =
        LoadGeometry(options.GeometryStateFilePath);

    private bool _geometrySeeded;

    private static (int Left, int Right, int FrameWidth, int FrameHeight, double? Bottom, double? FloorOffset)? LoadGeometry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var filePath = ResolveGeometryPath(path);
            if (!File.Exists(filePath)) return null;
            var node = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(filePath));
            return (
                node.GetProperty("left").GetInt32(), node.GetProperty("right").GetInt32(),
                node.TryGetProperty("frame_width", out var width) ? width.GetInt32() : 0,
                node.TryGetProperty("frame_height", out var height) ? height.GetInt32() : 0,
                node.TryGetProperty("jar_bottom_px", out var bottom) && bottom.ValueKind == JsonValueKind.Number
                    ? bottom.GetDouble() : null,
                node.TryGetProperty("dough_floor_offset_px", out var floorOffset) && floorOffset.ValueKind == JsonValueKind.Number
                    ? floorOffset.GetDouble() : null);
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
            var frame = _referenceFrameSize.GetValueOrDefault();
            File.WriteAllText(filePath, JsonSerializer.Serialize(new
            {
                left, right, frame_width = frame.Width, frame_height = frame.Height,
                jar_bottom_px = _referenceJarBottomPx,
                dough_floor_offset_px = HeldFloorOffset()
            }));
            _persistedGeometry = (left, right, frame.Width, frame.Height, _referenceJarBottomPx, HeldFloorOffset());
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
        _lastMeasuredFloorRow = null;
        var measurement = MeasureFrame(jpegBytes, now, sessionBaselineHeightPx);
        // Scene changes raised while measuring clear the floor history first, so this frame's
        // floor joins the new scene's samples.
        return measurement is null ? null : ApplyDoughFloor(measurement);
    }

    private LevelMeasurement? MeasureFrame(byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx)
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
        _proposedColumn = null;
        _freshColumnExtent = null;
        _freshWarmFront = null;
        _resetWarmFrontOnAccept = false;
        _unverifiedExtent = false;
        if (_referenceFrameSize is null
            && _persistedGeometry is { FrameWidth: > 0, FrameHeight: > 0 } saved)
        {
            _referenceFrameSize = (saved.FrameWidth, saved.FrameHeight);
            _referenceJarBottomPx = saved.Bottom;
            SeedFloor(saved.FloorOffset);
        }
        var frameSize = (Width: rawColor.Width, Height: rawColor.Height);
        if (_referenceFrameSize is { } reference && reference != frameSize)
        {
            if ((long)reference.Width * frameSize.Height == (long)reference.Height * frameSize.Width)
            {
                // Sampling resolution is not a new scene. Keep every pixel-based state
                // and the debug image in the established coordinate system.
                Cv2.Resize(rawColor, rawColor, new Size(reference.Width, reference.Height),
                    0, 0, InterpolationFlags.Linear);
                frameSize = reference;
            }
            else
            {
                _recentWarmColumns.Clear();
                _recentWarmFronts.Clear();
                _pendingWarmMove.Clear();
                _floorOffsets.Clear();
                _persistedGeometry = null;
                _referenceJarBottomPx = null;
                _warmColumnSupport = 0;
                _geometrySeeded = true;
                _referenceFrameSize = frameSize;
                SceneChanged?.Invoke();
            }
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
        if (_referenceFrameSize is null && _persistedGeometry is { } legacy && legacy.Right >= img.Width)
        {
            // Older geometry files have no image size. A smaller startup thumbnail
            // cannot establish a new pixel basis for their existing feeding session.
            LastOutcome = "no_jar";
            LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
            return null;
        }
        var detectedGlow = DetectGlowBlob(img);
        // Night frames without backlight are uniformly dark: nothing bright enough to be
        // a lit jar (P90 floor) and no meaningful contrast (P90-P10 floor). The edge-energy
        // method would "detect" noise there and pollute the growth series. Percentile-based
        // so a backlit night frame (dark room, bright jar) still passes.
        if (detectedGlow is null && (p90 < options.MinFrameIntensity || p90 - p10 < options.MinFrameContrast))
        {
            _pendingWarmMove.Clear();
            LastOutcome = "dark_frame";
            LastDiagnostics = WithFrameStats(new DetectionDiagnostics("dark", 0, null, null), frameStats);
            if (options.DebugSaveAnnotatedImages)
                SaveDebugImage(img, null, null, null, null, now, null);
            return null;
        }
        if (detectedGlow is { } glow)
        {
            var litColumn = FindBacklitColumn(img, glow);
            var litMeasurement = litColumn is not null
                ? MeasureBacklit(img, glow, litColumn, now, frameStats, sessionBaselineHeightPx)
                : null;
            if (litMeasurement is not null)
            {
                if (!AcceptGeometryMeasurement(litMeasurement, litColumn!, frameSize))
                {
                    LastOutcome = "no_jar";
                    return null;
                }
                LastOutcome = "detected";
                return litMeasurement;
            }
            _pendingWarmMove.Clear();
            // Never reinterpret a recognized light source as a warm wall or edge surface.
            LastOutcome = litColumn is null ? "no_jar" : "no_surface";
            LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
            SaveDebugImage(img, litColumn, null, litColumn?.Bottom, null, now, null);
            return null;
        }
        using var blurred = new Mat();
        Cv2.GaussianBlur(img, blurred, new Size(5, 5), 0);
        using var visWarm = options.DebugHighlightDough ? ComputeVisWarmMask(imgColor, img) : null;
        // Brightness skew is only a fallback hint. A small daylight jar on a dark
        // background has the same histogram, so it must not disable body geometry.
        var backlitScene = frameStats.Median is { } backlitMed && frameStats.P90 is { } backlitP90
            && backlitP90 - backlitMed > 0.5 * backlitMed;
        // The verified light-source/rim path above wins. Otherwise try the warm body
        // before interpreting room brightness or wall edges.
        // A full-frame fallback needs independently confirmed column/base evidence;
        // colour alone also selects the wooden stand while the jar is absent.
        var genuineColumnEvidence = false;
        {
            using var edges = AutoCanny(blurred, medianIntensity, 1.0);
            var warmColumn = FindJarColumnByWarmExtent(
                imgColor, img, img.Width, img.Height,
                options.WarmColumnSaturationStep, options.WarmColumnMinFraction,
                options.NeutralReferenceCeiling);
            if (warmColumn is not null)
            {
                var measurement = MeasureWithinColumn(edges, img, imgColor, warmColumn,
                    now, frameStats, sessionBaselineHeightPx, visWarm);
                // A colour proposal alone does not establish a physical container.
                genuineColumnEvidence = _lastColumnBottomConfirmed;
                if (measurement is not null)
                {
                    if (!AcceptGeometryMeasurement(measurement, warmColumn, frameSize))
                    {
                        LastOutcome = "no_jar";
                        return null;
                    }
                    LastOutcome = "detected";
                    return measurement;
                }
                _pendingWarmMove.Clear();
                // A cold-start colour patch is only a geometry proposal. Once the
                // column has three supporting samples, a contour miss cannot authorize
                // switching to a wall or unrelated fallback column.
                if (_warmColumnSupport >= 3 || _proposedColumn is not null)
                {
                    LastOutcome = "no_surface";
                    LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
                    SaveDebugImage(img, warmColumn, null, warmColumn.Bottom, null, now, visWarm);
                    return null;
                }
            }
            if (warmColumn is null && _warmColumnSupport >= 3)
            {
                _pendingWarmMove.Clear();
                LastOutcome = "no_jar";
                LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
                SaveDebugImage(img, null, null, null, null, now, null);
                return null;
            }
        }
        if (backlitScene)
        {
            var darkColumn = FindJarColumnByDarkExtent(img, img.Width, img.Height, options.DarkBandMaxIntensity);
            if (darkColumn is not null)
            {
                using var edges = AutoCanny(blurred, medianIntensity, 1.0);
                var measurement = MeasureWithinColumn(edges, img, imgColor, darkColumn,
                    now, frameStats, sessionBaselineHeightPx, visWarm, suppressWarmPath: true);
                genuineColumnEvidence = genuineColumnEvidence || _lastColumnBottomConfirmed;
                if (measurement is not null)
                {
                    AcceptGeometryMeasurement(measurement, darkColumn, frameSize);
                    LastOutcome = "detected";
                    return measurement;
                }
            }
        }
        // Transparent-wall evidence is useful only after the specific dough geometry
        // failed; lit stucco beside a backlit jar must never be interpreted as walls.
        if (!backlitScene)
        {
            foreach (var relaxation in new[] { 1.0, 0.6 })
            {
                using var edges = AutoCanny(blurred, medianIntensity, relaxation);
                var column = FindJarColumn(edges, img.Width, img.Height, relaxation);
                if (column is null) continue;
                genuineColumnEvidence = true;
                var measurement = MeasureWithinColumn(edges, img, imgColor, column,
                    now, frameStats, sessionBaselineHeightPx, visWarm);
                if (measurement is not null)
                {
                    AcceptGeometryMeasurement(measurement, column, frameSize);
                    LastOutcome = "detected";
                    return measurement;
                }
            }
        }
        // A wall-free column can refine a supported jar, not invent one from the shelf.
        if (genuineColumnEvidence)
        {
            foreach (var relaxation in new[] { 1.0, 0.6 })
            {
                using var edges = AutoCanny(blurred, medianIntensity, relaxation);
                var fallbackColumn = BuildFallbackColumn(img);
                var measurement = MeasureWithinColumn(edges, img, imgColor, fallbackColumn,
                    now, frameStats, sessionBaselineHeightPx, visWarm, suppressWarmPath: backlitScene);
                if (measurement is not null)
                {
                    AcceptGeometryMeasurement(measurement, fallbackColumn, frameSize);
                    LastOutcome = "detected";
                    return measurement;
                }
            }
        }
        _pendingWarmMove.Clear();
        LastOutcome = "no_surface";
        LastDiagnostics = WithFrameStats(new DetectionDiagnostics("none", 0, null, null), frameStats);
        if (options.DebugSaveAnnotatedImages)
            SaveDebugImage(img, null, null, null, null, now, visWarm);
        return null;
    }

    private bool AcceptGeometryMeasurement(
        LevelMeasurement measurement, JarColumn column, (int Width, int Height) frameSize)
    {
        var moved = _proposedColumn is not null;
        if (moved && _referenceJarBottomPx is { } previousBottom
            && Math.Abs(measurement.JarBottomPx - previousBottom) <= Math.Max(6, frameSize.Height * 0.01))
        {
            // Colour width can change with lighting; the supported physical base is
            // held for the whole scene instead of walking with each noisy estimate.
            moved = false;
        }
        if (moved)
        {
            var tolerance = Math.Max(10, (column.Right - column.Left) * 0.1);
            var candidatesAgree = true;
            foreach (var pending in _pendingWarmMove)
            {
                if (Math.Abs(pending.Left - column.Left) <= tolerance
                    && Math.Abs(pending.Right - column.Right) <= tolerance) continue;
                candidatesAgree = false;
                break;
            }
            if (!candidatesAgree) _pendingWarmMove.Clear();
            _pendingWarmMove.Enqueue((column.Left, column.Right));
            if (_pendingWarmMove.Count < 3) return false;
            _recentWarmColumns.Clear();
            _recentWarmFronts.Clear();
            _pendingWarmMove.Clear();
            _floorOffsets.Clear();
            _recentWarmColumns.Enqueue((column.Left, column.Right));
            _warmColumnSupport = 0;
            SceneChanged?.Invoke();
        }
        else
        {
            _pendingWarmMove.Clear();
            var width = column.Right - column.Left;
            if (column.Kind is "warm" or "backlit" && _freshColumnExtent is { } fresh
                && fresh.Right - fresh.Left >= width * 0.75 && fresh.Right - fresh.Left <= width * 1.25)
                _recentWarmColumns.Enqueue(fresh);
            else if (column.Kind is "backlit")
                _recentWarmColumns.Enqueue((column.Left, column.Right));
            while (_recentWarmColumns.Count > WarmColumnHistorySize) _recentWarmColumns.Dequeue();
        }
        _referenceFrameSize ??= frameSize;
        if ((moved || _referenceJarBottomPx is null)
            && (column.Kind is "backlit" || _lastColumnBottomConfirmed))
            _referenceJarBottomPx = measurement.JarBottomPx;
        if (LastDiagnostics?.Method is "warm" && _freshWarmFront is { } front)
        {
            if (_resetWarmFrontOnAccept) _recentWarmFronts.Clear();
            _recentWarmFronts.Enqueue(front);
            while (_recentWarmFronts.Count > 9) _recentWarmFronts.Dequeue();
        }
        if (column.Kind is "warm" or "backlit")
        {
            _warmColumnSupport = Math.Min(3, _warmColumnSupport + 1);
            var left = DensestCluster(_recentWarmColumns.Select(c => c.Left), 3);
            var right = DensestCluster(_recentWarmColumns.Select(c => c.Right), 3);
            if (_persistedGeometry is not { } saved || saved.Left != left || saved.Right != right
                || saved.FrameWidth != frameSize.Width || saved.FrameHeight != frameSize.Height
                || saved.Bottom != _referenceJarBottomPx)
                PersistGeometry(left, right);
        }
        return true;
    }

    private LevelMeasurement? MeasureWithinColumn(
        Mat edges,
        Mat gray,
        Mat color,
        JarColumn jarColumn,
        DateTimeOffset now,
        (double? Mean, double? Median, double? P10, double? P90) frameStats,
        double? sessionBaselineHeightPx,
        Mat? visWarm,
        bool suppressWarmPath = false)
    {
        _lastColumnBottomConfirmed = false;
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
        var doughTop = FindDoughSurface(columnEdges, columnGray, columnColor, frameStats, suppressWarmPath);
        if (doughTop is null)
        {
            SaveDebugImage(gray, jarColumn, null, null, null, now, visWarm);
            return null;
        }
        var anchorY = jarColumn.Top + doughTop.Value;
        var jarBottomCandidate = FindJarBottom(columnEdges, columnGray, doughTop.Value, rect.Height - 1);
        // A genuinely confirmed physical base edge (not just a defaulted/guessed one) is
        // real evidence that this column is an actual container, independent of whether
        // the rest of this specific measurement attempt then succeeds — see Measure's use
        // for the full-frame fallback column's evidence gate.
        _lastColumnBottomConfirmed = jarBottomCandidate is not null;
        if (jarBottomCandidate is null)
        {
            if (jarColumn.Kind is "warm")
            {
                // An unconfirmed warm-colour extent with no visible physical base edge
                // (shadow/table-contact line) is a colour PROPOSAL, not a verified
                // container — e.g. a warm wooden stand with no jar on it.
                // Defaulting to the proposal's own guessed bottom would report a
                // plausible-looking height for something that was never a jar.
                SaveDebugImage(gray, jarColumn, null, null, null, now, visWarm);
                return null;
            }
            jarBottomCandidate = rect.Height - 1;
        }
        var jarBottom = jarBottomCandidate.Value;
        if (_unverifiedExtent && _referenceJarBottomPx is { } heldBottom
            && Math.Abs(jarColumn.Top + jarBottom - heldBottom) > Math.Max(6, gray.Height * 0.01))
        {
            // Background colour/shadow cannot relocate an established physical base.
            SaveDebugImage(gray, jarColumn, null, null, null, now, visWarm);
            return null;
        }
        jarColumn = jarColumn with { Bottom = jarColumn.Top + jarBottom };
        // Color positions the search window; luminance traces the visible boundary.
        // Both ambient and backlit measurements report the traced curve's middle third.
        var contourColumn = jarColumn.Kind is "dark" or "fallback"
            ? jarColumn with { Left = rect.Left + rect.Width / 4, Right = rect.Right - rect.Width / 4 }
            : jarColumn;
        var curve = TraceContentSurface(gray, contourColumn, anchorY, null, out var contrast);
        if (curve is null || curve.Count == 0)
        {
            SaveDebugImage(gray, jarColumn, null, null, null, now, visWarm);
            return null;
        }
        var levelRow = CurveDeepestRow(curve,
            jarColumn with { Left = curve[0].X, Right = curve[^1].X });
        if (jarColumn.Kind is "dark" or "fallback")
            jarColumn = jarColumn with { Left = curve[0].X, Right = curve[^1].X };
        if (LastDiagnostics is { } measuredDiagnostics)
            LastDiagnostics = measuredDiagnostics with { BandContrast = contrast, FinalRow = levelRow - jarColumn.Top };
        // The jar's physical bottom and the dough floor don't move between frames, so the
        // session-start dough surface can be re-derived in this frame's coordinates. Heights
        // are measured from the dough floor once the scene's floor offset is known.
        var heightBaseRow = jarColumn.Top + jarBottom - (HeldFloorOffset() ?? 0);
        var sessionStartSurfaceY = sessionBaselineHeightPx is not null
            ? (int?)Math.Round(heightBaseRow - sessionBaselineHeightPx.Value)
            : null;
        SaveDebugImage(
            gray,
            jarColumn,
            levelRow,
            jarColumn.Top + jarBottom,
            sessionStartSurfaceY,
            now,
            visWarm,
            curve);
        _lastMeasuredFloorRow = MeasureDoughFloorRow(color, gray, rect, levelRow, jarColumn.Top + jarBottom);
        return new LevelMeasurement(now, levelRow, jarColumn.Top, jarColumn.Top + jarBottom);
    }

    /// <summary>The dough body's lower end in the strip centered in the jar column: the last
    /// row of the contiguous warm run containing the row midway between surface and glass
    /// bottom. "Warm" is the column-extent mask (neutral reference plus
    /// <see cref="VisionOptions.WarmColumnSaturationStep"/>, warm tone), so a pale or backlit dough
    /// without a warm body yields null rather than a guess. A run that reaches the glass
    /// bottom is not a floor (warm table or foot reflection).</summary>
    private int? MeasureDoughFloorRow(Mat color, Mat gray, Rect rect, int surfaceRow, int jarBottomRow)
    {
        var bottomLimit = Math.Min(jarBottomRow, color.Height - 1);
        var midRow = (surfaceRow + bottomLimit) / 2;
        var stripX = rect.X + rect.Width / 4;
        var stripWidth = rect.Width / 2;
        if (surfaceRow < 0 || bottomLimit - midRow < 4 || stripWidth < 8
            || stripX + stripWidth > color.Width) return null;
        ComputeTopQuarterRefs(color, gray, out var satNeutral, out _);
        var threshold = Math.Min(satNeutral, options.NeutralReferenceCeiling)
            + options.WarmColumnSaturationStep;
        var minWarmPixels = options.WarmColumnMinFraction * stripWidth;
        var floor = -1;
        for (var y = midRow; y <= bottomLimit; y++)
        {
            var warm = 0;
            for (var x = stripX; x < stripX + stripWidth; x++)
            {
                var pixel = color.At<Vec3b>(y, x);
                var spread = Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2))
                    - Math.Min(pixel.Item0, Math.Min(pixel.Item1, pixel.Item2));
                if (spread > threshold && pixel.Item2 > pixel.Item0 + 1 && pixel.Item2 >= pixel.Item1) warm++;
            }
            if (warm < minWarmPixels)
            {
                if (y == midRow) return null;
                break;
            }
            floor = y;
        }
        return floor < 0 || floor >= bottomLimit ? null : floor;
    }

    /// <summary>The scene's glass-base-to-floor offset (px): the median of the recent measured
    /// offsets, or null until <see cref="FloorMinSamples"/> samples exist.</summary>
    private double? HeldFloorOffset() =>
        _floorOffsets.Count < FloorMinSamples ? null : Median(_floorOffsets.Select(o => (float)o).ToArray());

    private void SeedFloor(double? persistedOffset)
    {
        if (persistedOffset is not { } offset || _floorOffsets.Count > 0) return;
        for (var i = 0; i < FloorMinSamples; i++) _floorOffsets.Enqueue(offset);
    }

    /// <summary>Stamps the dough floor on the measurement: this frame's measured floor when its warm
    /// body shows one (it follows a moved jar at once), else the glass bottom less the scene's held
    /// offset. A surface at or below the floor is no dough measurement: the measurement then
    /// carries no floor, which the analyzer reports as unavailable once its session is floor-based.</summary>
    private LevelMeasurement ApplyDoughFloor(LevelMeasurement measurement)
    {
        double floor;
        if (_lastMeasuredFloorRow is { } measured && IsPlausibleFloorOffset(measurement.JarBottomPx - measured))
        {
            floor = measured;
            _floorOffsets.Enqueue(measurement.JarBottomPx - measured);
            while (_floorOffsets.Count > FloorHistorySize) _floorOffsets.Dequeue();
        }
        else if (HeldFloorOffset() is { } held)
        {
            floor = measurement.JarBottomPx - held;
        }
        else
        {
            return measurement;
        }
        if (HeldFloorOffset() is { } heldOffset && _persistedGeometry is { } saved
            && (saved.FloorOffset is not { } persisted || Math.Abs(persisted - heldOffset) >= 2))
            PersistGeometry(saved.Left, saved.Right);
        return floor <= measurement.DoughTopPx + 1 ? measurement : measurement with { DoughFloorPx = floor };
    }

    private bool IsPlausibleFloorOffset(double offset) =>
        offset >= 0 && (_referenceFrameSize is not { } size || offset <= size.Height * 0.12);

    private readonly record struct GlowBlob(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;
    }

    // A light source proposes a search region; it is never itself a surface measurement.
    private static GlowBlob? DetectGlowBlob(Mat gray)
    {
        using var hot = new Mat();
        Cv2.Threshold(gray, hot, 235, 255, ThresholdTypes.Binary);
        if (hot.CountNonZero() < Math.Max(8, gray.Width * (double)gray.Height * 0.001)) return null;
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(hot, labels, stats, centroids);
        var best = -1;
        var bestArea = gray.Width * (double)gray.Height * 0.001;
        for (var i = 1; i < count; i++)
        {
            var width = stats.At<int>(i, 2);
            var bottom = stats.At<int>(i, 1) + stats.At<int>(i, 3);
            var area = stats.At<int>(i, 4);
            if (width < gray.Width * 0.03 || width > gray.Width * 0.7 || bottom >= gray.Height - 12)
                continue;
            if (area <= bestArea) continue;
            bestArea = area;
            best = i;
        }
        return best < 0 ? null : new GlowBlob(
            stats.At<int>(best, 0), stats.At<int>(best, 1),
            stats.At<int>(best, 2), stats.At<int>(best, 3));
    }

    // The lit glass foot must be a separate, wide component below the source. This
    // rejects the illuminated wall and narrow reflection strips before measuring dough.
    private JarColumn? FindBacklitColumn(Mat gray, GlowBlob glow)
    {
        var height = gray.Height;
        var width = gray.Width;
        var top = glow.Bottom + Math.Max(3, (height - glow.Bottom) * 15 / 100);
        var left = Math.Max(0, glow.X - width * 3 / 10);
        var right = Math.Min(width, glow.Right + width * 3 / 10);
        if (height - top < 20 || right - left < 20) return null;
        using var region = gray[new Rect(left, top, right - left, height - top - 2)];
        var (_, _, p90) = ComputeIntensityStats(region);
        using var mask = new Mat();
        Cv2.Threshold(region, mask, Math.Max(100, p90), 255, ThresholdTypes.Binary);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect,
            new Size(Math.Max(3, 2 * (width / 360) + 1), Math.Max(1, 2 * (height / 600) + 1)));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var sourceCenter = (glow.X + glow.Right) / 2;
        var bestArea = 0;
        Rect? rim = null;
        for (var i = 1; i < count; i++)
        {
            var x = left + stats.At<int>(i, 0);
            var y = top + stats.At<int>(i, 1);
            var w = stats.At<int>(i, 2);
            var h = stats.At<int>(i, 3);
            var area = stats.At<int>(i, 4);
            if (w < width * 0.12 || w <= 3 * h || x >= sourceCenter || x + w <= sourceCenter)
                continue;
            // A higher reflection can be larger than the actual glass foot. The
            // lowest separate, wide component is the physical base candidate.
            if (rim is { } held && (y + h < held.Bottom
                || (y + h == held.Bottom && area <= bestArea))) continue;
            bestArea = area;
            rim = new Rect(x, y, w, h);
        }
        if (rim is not { } foot) return null;

        var padding = foot.Width * 22 / 100;
        var candidateLeft = Math.Max(2, foot.X - padding);
        var candidateRight = Math.Min(width - 3, foot.Right + padding);
        var rimTop = Math.Max(glow.Bottom + 8, foot.Y - Math.Max(3, height / 60));
        var rimBottom = Math.Min(height - 1, foot.Bottom + Math.Max(3, height / 120));
        using var rimStrip = gray[new Rect(foot.X + foot.Width / 4, rimTop,
            Math.Max(1, foot.Width / 2), rimBottom - rimTop)];
        var profile = MovingAverage(ReduceRowsMedian(rimStrip), Math.Max(3, 2 * (height / 288) + 1));
        var bottom = foot.Bottom;
        var strongestDrop = 0.0;
        for (var y = 1; y < profile.Length - 1; y++)
        {
            var drop = profile[y - 1] - profile[y + 1];
            if (drop <= strongestDrop) continue;
            strongestDrop = drop;
            bottom = rimTop + y;
        }
        if (strongestDrop < 8) return null;
        _freshColumnExtent = (candidateLeft, candidateRight);

        if (!_geometrySeeded)
        {
            _geometrySeeded = true;
            if (_persistedGeometry is { } saved && saved.Left >= 0 && saved.Right < width)
                _recentWarmColumns.Enqueue((saved.Left, saved.Right));
        }
        if (_recentWarmColumns.Count > 0)
        {
            var heldLeft = DensestCluster(_recentWarmColumns.Select(c => c.Left), 3);
            var heldRight = DensestCluster(_recentWarmColumns.Select(c => c.Right), 3);
            var moveTolerance = Math.Max(12, (heldRight - heldLeft) * 0.18);
            var moved = Math.Abs(candidateLeft - heldLeft) > moveTolerance
                && Math.Abs(candidateRight - heldRight) > moveTolerance;
            if (moved)
            {
                _proposedColumn = new JarColumn(candidateLeft, candidateRight,
                    Math.Max(2, height / 30), bottom, "backlit");
                return _proposedColumn;
            }
            else
            {
                candidateLeft = heldLeft;
                candidateRight = heldRight;
            }
        }
        return new JarColumn(candidateLeft, candidateRight, Math.Max(2, height / 30), bottom, "backlit");
    }

    private LevelMeasurement? MeasureBacklit(
        Mat gray, GlowBlob glow, JarColumn column, DateTimeOffset now,
        (double? Mean, double? Median, double? P10, double? P90) frameStats,
        double? sessionBaselineHeightPx)
    {
        var curve = TraceContentSurface(gray, column, glow.Bottom, glow, out var contrast);
        if (curve is null) return null;
        var tracedColumn = column with { Left = curve[0].X, Right = curve[^1].X };
        var level = CurveDeepestRow(curve, tracedColumn);
        LastDiagnostics = WithFrameStats(new DetectionDiagnostics("backlit", contrast, null, level), frameStats);
        var baselineY = sessionBaselineHeightPx is { } baseline
            ? (int?)Math.Round(column.Bottom - baseline) : null;
        SaveDebugImage(gray, column, level, column.Bottom, baselineY, now, null, curve);
        return new LevelMeasurement(now, level, column.Top, column.Bottom);
    }

    // Threshold the jar's own profile, not the dark room or LED-lit wall. The surface
    // must terminate a sustained foreground run connected to the lower dough body.
    private static List<Point>? TraceContentSurface(
        Mat gray, JarColumn column, int anchorY, GlowBlob? glow, out double contrast)
    {
        contrast = 0;
        var width = column.Right - column.Left;
        if (width < 12 || column.Bottom <= column.Top + 20) return null;
        var scaleHeight = Math.Min(gray.Height, Math.Max(40, width * 3 / 2));
        var inset = Math.Max(2, width / 10);
        using var strip = gray[new Rect(column.Left + inset, 0, width - 2 * inset, gray.Height)];
        var profile = ReduceRowsMedian(strip);
        // A weak colour ramp can put its anchor deep inside dough. Keep empty-glass
        // reference rows in the search rather than calibrating to that interior edge.
        var glassStart = column.Top + (column.Bottom - column.Top) / 2;
        var start = Math.Max(column.Top, glow is { } light
            ? light.Bottom - Math.Max(2, scaleHeight / 50)
            : Math.Min(glassStart, anchorY - Math.Max(12, scaleHeight / 10)));
        var end = Math.Min(gray.Height - 1, column.Bottom - Math.Max(8, (column.Bottom - start) * 12 / 100));
        if (end - start < 25) return null;
        var threshold = OtsuThreshold(profile.AsSpan(start, end - start));
        var maxGap = Math.Max(2, scaleHeight / 144);
        var gaps = 0;
        var boundary = -1;
        for (var y = end - 1; y >= start; y--)
        {
            if (profile[y] <= threshold) gaps = 0;
            else if (++gaps > maxGap)
            {
                boundary = y + gaps;
                break;
            }
        }
        if (boundary <= start + maxGap || end - boundary < scaleHeight * 0.08) return null;
        var refStart = Math.Max(start, boundary - Math.Max(10, scaleHeight / 30));
        var refEnd = Math.Max(refStart + 1, boundary - Math.Max(2, scaleHeight / 100));
        var reference = 0.0;
        for (var y = refStart; y < refEnd; y++) reference += profile[y];
        reference /= refEnd - refStart;
        var body = 0.0;
        var bodyStart = Math.Max(boundary + 1, end - Math.Max(8, scaleHeight / 20));
        for (var y = bodyStart; y < end; y++) body += profile[y];
        body /= end - bodyStart;
        contrast = reference - body;
        if (contrast < 8 || (glow is null && reference < 80)) return null;

        var halfWindow = Math.Max(8, scaleHeight * 65 / 1000);
        var lo = Math.Max(start, boundary - halfWindow);
        var hi = Math.Min(end, boundary + halfWindow);
        using var smoothed = new Mat();
        Cv2.Blur(gray, smoothed, new Size(1, Math.Max(3, 2 * (scaleHeight / 288) + 1)));
        var points = new List<Point>(width);
        for (var x = column.Left + 4; x <= column.Right - 4; x++)
        {
            gaps = 0;
            var row = -1;
            for (var y = hi - 1; y >= lo; y--)
            {
                if (smoothed.At<byte>(y, x) <= threshold) gaps = 0;
                else if (++gaps > maxGap)
                {
                    row = y + gaps;
                    break;
                }
            }
            if (row <= lo + maxGap || row >= hi - maxGap) continue;
            var checkEnd = Math.Min(end, row + Math.Max(20, scaleHeight / 15));
            var foreground = 0;
            for (var y = row; y < checkEnd; y++)
                if (smoothed.At<byte>(y, x) <= threshold) foreground++;
            if (foreground * 10 >= (checkEnd - row) * 7) points.Add(new Point(x, row));
        }
        var minimumPoints = Math.Max(8, width / 5);
        if (points.Count < minimumPoints) return null;
        var median = MedianOr(points);
        var tolerance = Math.Max(10, scaleHeight * 3 / 100);
        var keep = 0;
        for (var i = 0; i < points.Count; i++)
            if (Math.Abs(points[i].Y - median) <= tolerance) points[keep++] = points[i];
        points.RemoveRange(keep, points.Count - keep);
        if (points.Count < minimumPoints) return null;
        var tracedColumn = column with { Left = points[0].X, Right = points[^1].X };
        if (tracedColumn.Right - tracedColumn.Left < width / 3) return null;
        var curve = BuildSurfaceArc(points, tracedColumn);
        // The front-glass arc is deepest centrally. A strong upward bow is a smear
        // or reflection outline, not the front surface; minor tilt/noise is tolerated.
        var edgeMean = (curve[0].Y + curve[^1].Y) / 2.0;
        var center = curve[curve.Count / 2].Y;
        var bowTolerance = Math.Max(4, (column.Bottom - column.Top) * 0.025);
        return center < edgeMean - bowTolerance ? null : curve;
    }

    private static int OtsuThreshold(ReadOnlySpan<float> values)
    {
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        double sum = 0;
        foreach (var value in values)
        {
            var bin = Math.Clamp((int)Math.Round(value), 0, 255);
            histogram[bin]++;
            sum += bin;
        }
        var weight = 0;
        double partial = 0;
        var bestVariance = -1.0;
        var threshold = 0;
        for (var bin = 0; bin < histogram.Length - 1; bin++)
        {
            weight += histogram[bin];
            partial += bin * histogram[bin];
            var otherWeight = values.Length - weight;
            if (weight == 0 || otherWeight == 0) continue;
            var difference = partial / weight - (sum - partial) / otherWeight;
            var variance = weight * (double)otherWeight * difference * difference;
            if (variance <= bestVariance) continue;
            bestVariance = variance;
            threshold = bin;
        }
        return threshold;
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
        double warmColumnMinFraction,
        double neutralReferenceCeiling)
    {
        // Seed once from the persisted column so a camera move made while the add-on was
        // down is caught by the same disagreement logic as a live bump. The seed ages out
        // of the window after WarmColumnHistorySize frames.
        if (!_geometrySeeded)
        {
            _geometrySeeded = true;
            if (_persistedGeometry is { } seededColumn)
                _recentWarmColumns.Enqueue((seededColumn.Left, seededColumn.Right));
        }
        ComputeTopQuarterRefs(color, gray, out var satNeutral, out var grayBright);
        satNeutral = Math.Min(satNeutral, neutralReferenceCeiling);
        using var saturation = SaturationMap(color);
        if (saturation.Empty()) return null;

        // Dough mask = warm OR dark, then the dough's row band: the longest run of rows whose
        // dough coverage is at least half the width. A fixed zone would dilute the column
        // fractions whenever the dough sits low (fresh feed): most zone rows are empty
        // glass, and a column's fraction drops below acceptance even though it is solidly
        // dough across the actual band.
        using var warmMask = new Mat();
        Cv2.Threshold(saturation, warmMask, satNeutral + warmColumnSaturationStep, 255, ThresholdTypes.Binary);
        // Spread alone also selects cool-tinted walls. Geometry needs genuinely warm
        // body pixels; surface localization retains its independently calibrated mask.
        for (var y = 0; y < warmMask.Rows; y++)
        for (var x = 0; x < warmMask.Cols; x++)
        {
            if (warmMask.At<byte>(y, x) == 0) continue;
            var pixel = color.At<Vec3b>(y, x);
            if (pixel.Item2 <= pixel.Item0 + 1 || pixel.Item2 < pixel.Item1)
                warmMask.Set(y, x, (byte)0);
        }
        using var darkMask = new Mat();
        Cv2.Threshold(gray, darkMask, grayBright - 18.0, 255, ThresholdTypes.BinaryInv);
        using var doughMask = new Mat();
        Cv2.Max(warmMask, darkMask, doughMask);
        var rowCoverage = CoverageProfile(doughMask);
        var bandTop = -1;
        var bandBottom = -1;
        var bandStart = -1;
        var bestBand = 0;
        Rect? componentBand = null;
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
        if (bandTop < 0 || bestBand < frameHeight * 0.05)
        {
            // A small jar need not cover half the whole frame. Locate a compact warm
            // body first, then measure column coverage over that body's own row band.
            componentBand = FindWarmBodyComponent(warmMask);
            if (componentBand is not { } bodyBand) return null;
            bandTop = bodyBand.Y;
            bandBottom = bodyBand.Bottom - 1;
            Cv2.Max(warmMask, darkMask, doughMask);
        }
        var minJarWidth = (int)(frameWidth * 0.04);
        var bandRect = new Rect(0, bandTop, frameWidth, bandBottom - bandTop + 1);
        int left;
        int right;
        // Warm-first: the color signal is the most specific dough evidence — everything
        // else in the scene is neutral white. A warm run wide enough to be the jar
        // interior defines the column, then the shadowed dough right of the warm edge
        // (real dough, but dark) is recovered by walking the warm extent outward over
        // the dough union — capped and gated so a bridging shadow can't drag the edge
        // onto background structures (observed drift: door frame at 1203 while the
        // dough ends at ~800). The union alone only takes over when the dough is too
        // pale for the color filter (fresh feed).
        using var bandMask = doughMask[bandRect];
        using var zoneColMeans = new Mat();
        Cv2.Reduce(bandMask, zoneColMeans, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
        if (!zoneColMeans.GetArray(out float[] zoneMeans) || zoneMeans.Length != frameWidth) return null;
        using var warmBand = warmMask[bandRect];
        using var warmColMeans = new Mat();
        Cv2.Reduce(warmBand, warmColMeans, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
        if (warmColMeans.GetArray(out float[] warmMeans) && warmMeans.Length == frameWidth)
        {
            var warmColumns = SmoothColumns(warmMeans, warmColumnMinFraction, frameWidth);
            (left, right, var warmLength) = LongestRun(warmColumns);
            if (warmLength < (componentBand is not null
                ? minJarWidth : Math.Max(minJarWidth, (int)(frameWidth * 0.25))))
            {
                // Pale dough: fall back to warm ∪ dark. The dark extent is shadow-prone on
                // the right, but the pale phase is temporary and the surface strip survives
                // the wall minority via the row medians.
                var doughColumns = SmoothColumns(zoneMeans, warmColumnMinFraction, frameWidth);
                (left, right, var doughLength) = LongestRun(doughColumns);
                if (doughLength < minJarWidth) return null;
            }
        }
        else
        {
            return null;
        }
        // Densest-cluster mode per edge (±3 px): good-light frames agree on the dough edge,
        // washout frames scatter to narrower extents, occasional pollution scatters wider —
        // the mode sits at the true edge and stays put while good frames dominate the
        // window; a lasting regime change migrates the mode smoothly.
        var columnLeft = _recentWarmColumns.Count == 0 ? left
            : DensestCluster(_recentWarmColumns.Select(c => c.Left), 3);
        var columnRight = _recentWarmColumns.Count == 0 ? right
            : DensestCluster(_recentWarmColumns.Select(c => c.Right), 3);
        if (columnRight - columnLeft < minJarWidth) return null;
        var marginY = componentBand is { } component
            ? Math.Max(2, component.Y - component.Width / 4)
            : Math.Max(2, frameHeight / 30);
        var columnBottom = componentBand is { } bounded
            ? Math.Min(frameHeight - 2, bounded.Bottom + Math.Max(8, bounded.Width / 12))
            : frameHeight - marginY;
        _freshColumnExtent = (left, right);
        var moveTolerance = Math.Max(12, (columnRight - columnLeft) * 0.18);
        var heldWidth = columnRight - columnLeft;
        var freshWidth = right - left;
        var moved = Math.Abs(left - columnLeft) > moveTolerance
            && Math.Abs(right - columnRight) > moveTolerance;
        var resized = freshWidth < heldWidth * 0.75 || freshWidth > heldWidth * 1.25;
        if (_recentWarmColumns.Count > 0 && (moved || resized))
        {
            var body = componentBand ?? FindWarmBodyComponent(warmMask);
            if (body is not { } supported || supported.Width < freshWidth * 0.65
                || supported.Bottom >= frameHeight - 2
                || supported.Bottom <= bandTop || supported.Y > bandBottom)
            {
                _unverifiedExtent = true;
                return new JarColumn(columnLeft, columnRight, marginY, columnBottom, "warm");
            }
            left = Math.Max(left, supported.X);
            right = Math.Min(right, supported.Right - 1);
            _freshColumnExtent = (left, right);
            freshWidth = right - left;
            moved = Math.Abs(left - columnLeft) > moveTolerance
                && Math.Abs(right - columnRight) > moveTolerance;
            resized = freshWidth < heldWidth * 0.75 || freshWidth > heldWidth * 1.25;
            if (!moved && !resized)
                return new JarColumn(columnLeft, columnRight, marginY, columnBottom, "warm");
            _proposedColumn = new JarColumn(left, right, marginY, columnBottom, "warm");
            return _proposedColumn;
        }
        return new JarColumn(columnLeft, columnRight, marginY, columnBottom, "warm");
    }

    private Rect? FindWarmBodyComponent(Mat mask)
    {
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centers);
        var minimumWidth = Math.Max(12, mask.Width * options.MinJarWidthFraction);
        var bestArea = mask.Width * (double)mask.Height * 0.002;
        Rect? best = null;
        for (var i = 1; i < count; i++)
        {
            var x = stats.At<int>(i, 0);
            var y = stats.At<int>(i, 1);
            var width = stats.At<int>(i, 2);
            var height = stats.At<int>(i, 3);
            var area = stats.At<int>(i, 4);
            if (width < minimumWidth || height < Math.Max(12, mask.Height * 0.04)
                || width < height * 0.5 || width > height * 4
                || x <= 2 || y <= 2 || x + width >= mask.Width - 2 || area <= bestArea)
                continue;
            bestArea = area;
            best = new Rect(x, y, width, height);
        }
        return best;
    }

    /// <summary>Column from the dough's DARK silhouette extent — the backlit-scene path:
    /// with the LED behind the jar the dough reads as a dark mass (no usable warm color),
    /// while the LED-lit wall passes the warm filter and hijacks the warm-extent column.
    /// Uses the absolute dark threshold (the same statistic the band method thresholds
    /// against), so it is immune to the dark room dominating the relative references.</summary>
    private static JarColumn? FindJarColumnByDarkExtent(Mat gray, int frameWidth, int frameHeight, double darkBandMaxIntensity)
    {
        var zoneTop = (int)(frameHeight * 0.50);
        var zoneBottom = Math.Min(frameHeight - 1, (int)(frameHeight * 0.95));
        var minJarWidth = (int)(frameWidth * 0.04);
        var border = Math.Max(4, frameWidth / 40);
        var bestStart = -1;
        var bestLength = 0;
        var runStart = -1;
        for (var x = border; x < frameWidth - border; x++)
        {
            // Bottom-contiguity: the dark run must END at the zone bottom — the dough
            // rests on the jar base. The dark ROOM columns break at the lit shelf in
            // front of them and never accumulate a long contiguous run from the bottom.
            var run = 0;
            for (var y = zoneBottom; y >= zoneTop; y--)
            {
                if (gray.At<byte>(y, x) < darkBandMaxIntensity) run++;
                else break;
            }
            var darkEnough = run * 2 >= (zoneBottom - zoneTop); // ≥50 % of the zone dark from the bottom
            if (darkEnough)
            {
                if (runStart < 0) runStart = x;
                var length = x - runStart + 1;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = runStart;
                }
            }
            else
            {
                runStart = -1;
            }
        }
        if (bestStart < 0 || bestLength < minJarWidth) return null;
        var marginY = Math.Max(2, frameHeight / 30);
        return new JarColumn(bestStart, bestStart + bestLength - 1, marginY, frameHeight - marginY, "dark");
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
        satNeutral = Math.Min(satNeutral, options.NeutralReferenceCeiling);
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
            using var max = new Mat();
            Cv2.Max(channels[0], channels[1], max);
            Cv2.Max(max, channels[2], max);
            using var min = new Mat();
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


    /// <summary>Builds the surface curve from the traced outline: median rows of the left
    /// quarter, the middle and the right quarter, then the parabola through those three
    /// anchors (Lagrange). The camera may be slightly rolled, so the anchors capture BOTH
    /// the glass-bulge bow and any tilt — the curve passes through all three. Nearly
    /// collinear anchors degrade to a straight line naturally. Resampled per column.</summary>
    private static List<Point> BuildSurfaceArc(List<Point> points, JarColumn jarColumn)
    {
        var width = jarColumn.Right - jarColumn.Left;
        var leftEnd = jarColumn.Left + width / 4;
        var rightStart = jarColumn.Right - width / 4;
        var leftYs = new List<int>();
        var midYs = new List<int>();
        var rightYs = new List<int>();
        foreach (var p in points)
        {
            if (p.X <= leftEnd) leftYs.Add(p.Y);
            else if (p.X >= rightStart) rightYs.Add(p.Y);
            else midYs.Add(p.Y);
        }
        leftYs.Sort();
        midYs.Sort();
        rightYs.Sort();
        var yLeft = leftYs.Count > 0 ? leftYs[leftYs.Count / 2] : MedianOr(points);
        var yMid = midYs.Count > 0 ? midYs[midYs.Count / 2] : MedianOr(points);
        var yRight = rightYs.Count > 0 ? rightYs[rightYs.Count / 2] : MedianOr(points);
        var xL = jarColumn.Left + width / 8;
        var xC = (jarColumn.Left + jarColumn.Right) / 2.0;
        var xR = jarColumn.Right - width / 8;
        var result = new List<Point>(jarColumn.Right - jarColumn.Left - 7);
        for (var x = jarColumn.Left + 4; x <= jarColumn.Right - 4; x++)
        {
            result.Add(new Point(x, (int)Math.Round(Lagrange3(xL, yLeft, xC, yMid, xR, yRight, x))));
        }
        return result;
    }

    /// <summary>Median y of the whole traced outline (the straight-surface fallback).</summary>
    private static int MedianOr(List<Point> points)
    {
        if (points.Count == 0) return 0;
        var ys = new List<int>(points.Count);
        foreach (var p in points) ys.Add(p.Y);
        ys.Sort();
        return ys[ys.Count / 2];
    }

    /// <summary>Value of the parabola through (x1,y1), (x2,y2), (x3,y3) at x — Lagrange's
    /// form, exact for the three anchors and smooth in between.</summary>
    private static double Lagrange3(double x1, double y1, double x2, double y2, double x3, double y3, double x)
    {
        return y1 * (x - x2) * (x - x3) / ((x1 - x2) * (x1 - x3))
             + y2 * (x - x1) * (x - x3) / ((x2 - x1) * (x2 - x3))
             + y3 * (x - x1) * (x - x2) / ((x3 - x1) * (x3 - x2));
    }

    /// <summary>Level row from a traced surface curve: the median y over the curve's
    /// middle third, where the arc's bottom (the front-glass contact) lives. The median
    /// rejects single-column spikes; the third keeps the higher side arcs out.</summary>
    private static int CurveDeepestRow(List<Point> curve, JarColumn jarColumn)
    {
        var width = jarColumn.Right - jarColumn.Left;
        var midLeft = jarColumn.Left + width / 3;
        var midRight = jarColumn.Right - width / 3;
        var ys = new List<int>();
        foreach (var p in curve)
        {
            if (p.X >= midLeft && p.X <= midRight) ys.Add(p.Y);
        }
        if (ys.Count == 0)
        {
            foreach (var p in curve) ys.Add(p.Y);
        }
        ys.Sort();
        return ys[ys.Count / 2];
    }

    private void SaveDebugImage(
        Mat image,
        JarColumn? jarColumn,
        int? doughSurfaceY,
        int? jarBottomY,
        int? sessionStartSurfaceY,
        DateTimeOffset now,
        Mat? visWarm,
        IReadOnlyList<Point>? surfaceCurve = null)
    {
        LastDebugImagePath = null;
        if (LastDiagnostics is { } diagnostics)
        {
            LastDiagnostics = diagnostics with
            {
                JarLeftPx = jarColumn?.Left,
                JarRightPx = jarColumn?.Right,
                JarColumnKind = jarColumn?.Kind
            };
        }
        using var color = new Mat();
        Cv2.CvtColor(image, color, ColorConversionCodes.GRAY2BGR);
        // Show what the color filter sees: orange = warm-tone pixels (the dough). Opt-in
        // (DebugHighlightDough): the translucent paint makes the scene hard to read for
        // the eye — the surface curve is drawn regardless.
        if (visWarm is not null && options.DebugHighlightDough)
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
            DrawSurfaceCurve(color, jarColumn, doughSurfaceY.Value, surfaceCurve);
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

    /// <summary>Draws the traced surface curve as a red polyline (the level plane inside
    /// the bulged glass projects as an arc). Falls back to the straight horizontal line
    /// when no curve was traced (dark frames, pale dough without a color signal).</summary>
    private static void DrawSurfaceCurve(Mat color, JarColumn jarColumn, int doughSurfaceY, IReadOnlyList<Point>? surfaceCurve)
    {
        if (surfaceCurve is { Count: > 1 })
        {
            for (var i = 1; i < surfaceCurve.Count; i++)
            {
                Cv2.Line(color, surfaceCurve[i - 1], surfaceCurve[i], Scalar.Red, 2);
            }
            return;
        }
        Cv2.Line(
            color,
            new Point(0, doughSurfaceY),
            new Point(color.Width, doughSurfaceY),
            Scalar.Red,
            2);
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
        (double? Mean, double? Median, double? P10, double? P90) frameStats,
        bool suppressWarmPath = false)
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
        if (!suppressWarmPath)
        {
            ComputeTopQuarterRefs(centralStripColor, centralStrip, out var satNeutral, out var _);
            if (satNeutral is { } satRef)
            {
                // The top quarter holds the condensation/smear zone above the dough; its
                // saturation pollutes the neutral reference (observed 8-10 instead of the
                // clean-glass 2-3) and silently stiffens the warm mask past its calibrated
                // threshold. Cap the reference so the calibrated step stays valid.
                satRef = Math.Min(satRef, options.NeutralReferenceCeiling);
                // Central-strip warm coverage only positions the contour search. The
                // reported surface comes from the lower-connected luminance outline,
                // so saturated body pixels cannot redefine the measurement basis.
                using var satMap = SaturationMap(centralStripColor);
                using var warmMask = new Mat();
                Cv2.Threshold(satMap, warmMask, satRef + options.MinWarmSaturationStep, 255, ThresholdTypes.Binary);
                warmCoverage = CoverageProfile(warmMask);
                if (InteriorBounds(warmMask, warmCoverage) is { } warmInterior)
                {
                    warmCoverage = CoverageProfile(warmMask[new Rect(warmInterior.Left, 0, warmInterior.Right - warmInterior.Left + 1, warmMask.Rows)]);
                }
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
            options.FrontEdgeCoverageFraction,
            suppressWarmPath);
        LastDiagnostics = WithFrameStats(diagnostics ?? new DetectionDiagnostics("none", 0, null, null), frameStats);
        // Exposure-flicker smoothing for the warm crossing only: the band/dark paths are
        // already gate-protected and their signals differ in kind, so mixing them into
        // one median would blur two different boundaries.
        if (result is { } rawRow && diagnostics?.Method == "warm")
        {
            _freshWarmFront = rawRow;
            Span<int> previous = stackalloc int[9];
            var previousCount = 0;
            foreach (var row in _recentWarmFronts) previous[previousCount++] = row;
            previous[..previousCount].Sort();
            if (previousCount >= 3 && rawRow - previous[previousCount / 2] > 50)
            {
                _resetWarmFrontOnAccept = true;
                return result;
            }
            if (_proposedColumn is not null) return result;

            Span<int> next = stackalloc int[9];
            var skip = _recentWarmFronts.Count == 9;
            var count = 0;
            foreach (var row in _recentWarmFronts)
            {
                if (skip) { skip = false; continue; }
                next[count++] = row;
            }
            next[count++] = rawRow;
            next[..count].Sort();
            result = next[count / 2];
        }
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

    /// <summary>Resolves the jar bottom in column coordinates from a visible physical edge
    /// (profile contrast step or horizontal Hough edge) within a band just above the
    /// column's own lower bound. Returns null when neither confirms an edge — the caller
    /// decides whether a last-resort default is acceptable for the column's kind. Picking
    /// any strong edge below the dough (e.g. the dough surface edge itself) collapses the
    /// height and poisons the rise series.</summary>
    private static int? FindJarBottom(Mat columnEdges, Mat columnGray, int doughTop, int fallbackBottom)
    {
        var bandStart = Math.Max(doughTop + 2, fallbackBottom - Math.Max(4, fallbackBottom / 4));
        var profileBottom = FindJarBottomFromProfile(columnGray, bandStart, fallbackBottom);
        if (profileBottom is not null) return profileBottom.Value;
        return FindJarBottomFromHorizontalEdge(columnEdges, bandStart, fallbackBottom);
    }

    // The ambient-lit glass foot is a dark shadow followed by the lighter shelf.
    // Reading its lower edge also works when defocus leaves too little Canny energy.
    private static int? FindJarBottomFromProfile(Mat gray, int start, int end)
    {
        var profile = MovingAverage(ReduceRowsMedian(gray), 5);
        var bestContrast = 8.0;
        int? bottom = null;
        for (var y = Math.Max(start + 3, 3); y <= Math.Min(end - 3, profile.Length - 4); y++)
        {
            var contrast = profile[y + 3] - profile[y - 3];
            if (contrast <= bestContrast) continue;
            bestContrast = contrast;
            bottom = Math.Min(end, y + 2);
        }
        return bottom;
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
            measurement.JarBottomPx + roiY,
            measurement.DoughFloorPx + roiY);
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
        double frontEdgeCoverageFraction,
        bool suppressWarmPath = false)
    {
        var longRun = (int)Math.Ceiling(Math.Max(4, rowEnergy.Count * minDarkBandFraction));
        // In backlit/night scenes the silhouette is contiguous with the jar base — scan
        // for it bottom-up (the top-down scan fires on dark glass patches ABOVE the glow).
        var bandTop = suppressWarmPath
            ? FindDoughBandTopBacklit(rowIntensity, out var bandContrast)
            : FindDoughBandTop(
                rowIntensity,
                out bandContrast,
                strongContrast,
                minAmbientContrast,
                darkBandMaxIntensity,
                minDarkBandFraction);
        // Warm and dark profiles propose an anchor, not the final level. A colored
        // body with a failed crossing stays unavailable rather than switching to a
        // different boundary. The accepted column is traced separately below.
        if (!suppressWarmPath)
        {
            var warmFront = FrontEdgeByWarmCoverage(warmCoverage, longRun, frontEdgeCoverageFraction);
            var warmPlateau = WarmCoveragePlateau(warmCoverage);
            if (warmFront is null && warmPlateau >= 0.5)
            {
                diagnostics = new DetectionDiagnostics("none", 0, null, null);
                return null;
            }
            // Backlit-scene guard: with the LED behind the jar the warm-toned GLOW above the
            // dough passes the warm mask and the crossing lands on the glow's top — 200+ px
            // above the real dough (observed live: readings 108-250 while the silhouette
            // sits at ~470). The dark dough silhouette against the glow is the band signal:
            // when a band top exists far BELOW the warm crossing, the warm evidence is the
            // glow, not the dough — fall through to the band path.
            if (warmFront is not null && bandTop is not null && warmFront.Value < bandTop.Value - 60)
            {
                warmFront = null;
            }
            if (warmFront is not null)
            {
                var snapped = SnapToEdge(rowEnergy, warmFront.Value);
                var bodyContrast = MeanCoverage(warmCoverage, warmFront.Value, Math.Max(8, longRun / 2));
                diagnostics = new DetectionDiagnostics("warm", bodyContrast, warmFront, snapped);
                return snapped;
            }
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
        // NOT in backlit/night scenes: the strongest edge there is the room→glow boundary
        // or wall texture, far above the dough (observed: readings 174-292 vs ~470).
        if (!suppressWarmPath)
        {
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
        diagnostics = new DetectionDiagnostics("none", bandContrast, bandTop, null);
        return bandTop;
    }

    /// <summary>Peak dough-coverage fraction of the warm profile (searching from 5 % of
    /// the rows down), the same statistic the warm crossing thresholds against. Distinguishes
    /// "colored body present" (≥ 0.5) from a fresh pale feed with no usable color.</summary>
    private static double WarmCoveragePlateau(float[]? coverage)
    {
        if (coverage is null || coverage.Length == 0) return 0;
        var searchStart = Math.Min(coverage.Length - 1, (int)(coverage.Length * 0.05));
        var plateau = 0f;
        for (var y = searchStart; y < coverage.Length; y++)
        {
            if (coverage[y] > plateau) plateau = coverage[y];
        }
        return plateau;
    }

    /// <summary>Front edge from the warm-coverage profile: the top of the LONGEST run of rows
    /// whose warm coverage reaches the solid-onset fraction of the plateau. The pale
    /// stir-smudge above the dough has warm patches, but they never form a sustained run —
    /// the body does (with small dips bridged). Returns null on a transient crossing
    /// failure even when a colored body is present — the caller decides whether to skip
    /// the frame or fall back to the dark band.</summary>
    private static int? FrontEdgeByWarmCoverage(float[]? coverage, int longRun, double frontEdgeCoverageFraction)
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
        // High solid-onset threshold over the strip profile: the dough's level plane
        // projects through the bulged glass as an arc whose lowest point is the
        // front-glass center — the user-confirmed surface ("am Rand flacher, vorne
        // tiefer, es müsste eine Kurve sein"). A high threshold (the same fraction the
        // dark path uses) skips the shallow side arcs of the ramp and lands on the
        // front-center level; lower thresholds land mid-arc and read too shallow.
        var threshold = frontEdgeCoverageFraction * plateau;
        var minSolid = Math.Max(4, longRun / 2);
        // The body's coverage dips briefly below any high threshold (dark streaks,
        // bubbles: 0.84-0.90 for a few rows). Bridges of up to 6 rows keep the run
        // continuous — without this, a high threshold splits the body and the crossing
        // falls through to the band path, whose boundary reads on a different basis.
        var runStart = -1;
        var gap = 0;
        var bestStart = -1;
        var bestLength = 0;
        for (var y = searchStart; y <= n; y++)
        {
            var on = y < n && coverage[y] >= threshold;
            if (on)
            {
                if (runStart < 0) runStart = y;
                gap = 0;
                var length = y - runStart + 1;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = runStart;
                }
            }
            else if (runStart >= 0)
            {
                gap++;
                if (gap > 6) runStart = -1;
            }
        }
        if (bestStart < 0 || bestLength < minSolid) return null;
        return bestStart;
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
        var mode = TopQuarterMode(smoothed);
        // The wall/glass above the dough must actually be bright for this method to
        // apply; otherwise the frame shows something we don't understand.
        return mode is { } level && level >= 100 ? (double?)level : null;
    }

    /// <summary>Modal value of the profile's top quarter WITHOUT an absolute brightness
    /// gate — the backlit scenes use it as the glow reference: the glow is bright
    /// RELATIVE to the dough silhouette at any exposure level.</summary>
    private static double? TopQuarterMode(double[] smoothed)
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

    /// <summary>Backlit variant of the band scan: the dough's dark silhouette is CONTIGUOUS
    /// with the jar base, so the scan walks UP from inside the body to the first sustained
    /// bright row (the glow behind the glass). The day-scene top-down scan cannot be used
    /// here: it fires on dark glass patches floating ABOVE the glow (observed: readings
    /// 108-250 while the silhouette sits at ~470). Returns null when the scan start is not
    /// inside a dark body (nothing trustworthy to walk from).</summary>
    public static int? FindDoughBandTopBacklit(
        IReadOnlyList<float> rowIntensity,
        out double contrast,
        double minAmbientContrast = 18.0)
    {
        contrast = 0;
        var smoothed = MovingAverage(rowIntensity, 7);
        var n = smoothed.Length;
        if (n < 10) return null;
        // RELATIVE reference (no absolute brightness gate): the glow above the dough is
        // the bright signal at any exposure level — the day-scene gate (mode ≥ 100) would
        // reject the dim backlit frames entirely (observed: no readings after the LED
        // went on at dusk).
        var brightLevel = TopQuarterMode(smoothed);
        if (brightLevel is null) return null;
        var line = brightLevel.Value - minAmbientContrast;
        var scanStart = Math.Min(n - 2, (int)(n * 0.85));
        if (smoothed[scanStart] >= line) return null;
        var y = scanStart;
        while (y > 1 && smoothed[y] < line) y--;
        var top = y + 1;
        if (top >= n) return null;
        contrast = brightLevel.Value - smoothed[top];
        return top;
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