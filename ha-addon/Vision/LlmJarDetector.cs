using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using OpenCvSharp;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Services;

namespace SourdoughMonitor.Vision;

/// <summary>Dough-level detector that asks a vision LLM to mark the whole jar outline and the
/// front dough surface as points, then computes the level from those points. The frame is
/// gated for darkness, cropped to the configured ROI and downscaled before upload to keep the
/// cost per call low. Replaces the classic <see cref="JarLevelDetector"/> in the live path;
/// that detector is kept in the repository but is not wired.
/// All reported coordinates are full-frame pixels of the first frame size seen.</summary>
public sealed class LlmJarDetector(
    VisionOptions vision,
    LlmOptions llm,
    IVisionLlmClient client,
    ILogger<LlmJarDetector> logger)
{
    private (int Width, int Height)? _reference;

    private readonly Queue<JarBox> _boxes = new();

    /// <summary>Raised when the camera view changed (jar outline jumped for several frames or
    /// the frame aspect ratio changed); the rise baseline no longer applies.</summary>
    public event Action? SceneChanged;

    public DetectionDiagnostics? LastDiagnostics { get; private set; }

    /// <summary>"detected", "dark_frame", "decode_failed", "no_jar", "no_surface",
    /// "geometry_warmup", "llm_rejected", "llm_error" or "llm_budget".</summary>
    public string LastOutcome { get; private set; } = "no_data";

    public byte[]? LatestAnnotatedImageBytes { get; private set; }

    public string? LastDebugImagePath { get; private set; }

    /// <summary>Billed cost of all calls since start (this run), as reported by OpenRouter.</summary>
    public double TotalCostUsd { get; private set; }

    /// <summary>Billed calls and cost per UTC day and in total; persisted across restarts.</summary>
    public LlmUsageTracker Usage { get; } = new(llm.UsageFilePath);

    /// <summary>How far apart the samples of one cycle are, over all cycles since start.
    /// Consistently small values mean the extra samples add cost without changing the level.</summary>
    public SampleSpreadStats Spread { get; } = new();

    public async Task<LevelMeasurement?> MeasureAsync(
        byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx, CancellationToken ct)
    {
        LastDebugImagePath = null;
        using var frame = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (frame.Empty())
        {
            LastOutcome = "decode_failed";
            LastDiagnostics = new DetectionDiagnostics("decode_failed", 0, null, null);
            return null;
        }
        NormaliseFrameSize(frame);
        var crop = CropRect(frame);
        using var cropped = frame[crop];
        using var gray = new Mat();
        Cv2.CvtColor(cropped, gray, ColorConversionCodes.BGR2GRAY);
        var (median, p10, p90) = FrameIntensity.Percentiles(gray);
        var stats = new DetectionDiagnostics("llm", 0, null, null)
        {
            FrameMean = Cv2.Mean(gray).Val0,
            FrameMedian = median,
            FrameP10 = p10,
            FrameP90 = p90
        };
        if (FrameIntensity.IsTooDark(p90, vision))
        {
            LastOutcome = "dark_frame";
            LastDiagnostics = stats with { Method = "dark" };
            return null;
        }
        var wanted = Math.Max(1, llm.SamplesPerCycle);
        if (BudgetExhausted(wanted))
        {
            LastOutcome = "llm_budget";
            LastDiagnostics = stats;
            logger.LogWarning("LLM call cap of {Cap} per day reached; skipping frame", llm.MaxCallsPerDay);
            return null;
        }

        var jpeg = EncodeForUpload(cropped);
        var cropTuple = (crop.X, crop.Y, crop.Width, crop.Height);
        var clock = Stopwatch.StartNew();
        var draws = await Task.WhenAll(
            Enumerable.Range(0, wanted).Select(i => DrawAsync(i, jpeg, cropTuple, ct)));
        clock.Stop();
        var billed = draws.Where(d => d.Completion is { FromCache: false }).ToList();
        var cost = billed.Sum(d => d.Completion!.CostUsd ?? 0);
        Usage.Record(billed.Count, cost, DateOnly.FromDateTime(DateTime.UtcNow));
        TotalCostUsd += cost;

        // A majority of the draws must agree that there is a measurable surface; otherwise the
        // most common rejection decides the outcome.
        var usable = draws.Where(d => d.Outcome == "detected").ToList();
        double? spread = usable.Count == 0
            ? null
            : usable.Max(d => d.Interpretation!.SurfaceRow!.Value) - usable.Min(d => d.Interpretation!.SurfaceRow!.Value);
        LlmInterpretation consensus;
        if (usable.Count * 2 > wanted)
        {
            consensus = Consensus(usable);
        }
        else
        {
            var pick = draws.Where(d => d.Outcome != "detected")
                .GroupBy(d => (d.Outcome, d.Reason))
                .OrderByDescending(g => g.Count())
                .First()
                .First();
            consensus = pick.Interpretation ?? new LlmInterpretation(pick.Outcome, pick.Reason, null, [], [], null);
        }
        var measurement = consensus.Outcome == "detected"
            ? Accept(consensus, now, out consensus)
            : null;
        if (spread is { } s) Spread.Observe(s, llm.SpreadWarnPx);
        logger.LogInformation(
            "LLM {Model}: {Ok}/{N} samples usable, levels [{Levels}], spread {Spread} px | running spread mean {Mean:F1}, "
            + "max {Max:F1} px, {Noisy}/{Cycles} cycles over {Warn} px | ${Cost}, {Ms} ms",
            llm.Model,
            usable.Count,
            wanted,
            string.Join(", ", usable.Select(d => d.Interpretation!.SurfaceRow!.Value.ToString("F0", CultureInfo.InvariantCulture))),
            spread?.ToString("F1", CultureInfo.InvariantCulture) ?? "-",
            Spread.Mean,
            Spread.Max,
            Spread.Noisy,
            Spread.Cycles,
            llm.SpreadWarnPx,
            cost.ToString("F5", CultureInfo.InvariantCulture),
            clock.ElapsedMilliseconds);
        return Finish(
            frame, crop, now, stats, consensus.Outcome, consensus.Reason,
            consensus, draws, clock.ElapsedMilliseconds, sessionBaselineHeightPx, spread, measurement);
    }

    /// <summary>Frames of another resolution but the same aspect ratio are scaled to the first
    /// frame's size so pixel baselines stay valid; a different aspect ratio is a new scene.</summary>
    private void NormaliseFrameSize(Mat frame)
    {
        var size = (frame.Width, frame.Height);
        if (_reference is not { } reference)
        {
            _reference = size;
            return;
        }
        if (reference == size) return;
        if ((long)reference.Width * size.Height == (long)reference.Height * size.Width)
        {
            Cv2.Resize(frame, frame, new Size(reference.Width, reference.Height), 0, 0, InterpolationFlags.Linear);
            return;
        }
        _reference = size;
        _boxes.Clear();
        SceneChanged?.Invoke();
    }

    private Rect CropRect(Mat frame)
    {
        var all = new Rect(0, 0, frame.Width, frame.Height);
        if (vision is { RoiX: { } x, RoiY: { } y, RoiWidth: { } w, RoiHeight: { } h })
        {
            var rect = new Rect(x, y, w, h) & all;
            if (rect.Width > 0 && rect.Height > 0) return rect;
        }
        return all;
    }

    private byte[] EncodeForUpload(Mat cropped)
    {
        var scale = Math.Min(1.0, (double)llm.ImageMaxEdge / Math.Max(cropped.Width, cropped.Height));
        using var scaled = new Mat();
        if (scale < 1.0)
        {
            Cv2.Resize(cropped, scaled,
                new Size(Math.Max(1, (int)Math.Round(cropped.Width * scale)),
                    Math.Max(1, (int)Math.Round(cropped.Height * scale))),
                0, 0, InterpolationFlags.Area);
        }
        else
        {
            cropped.CopyTo(scaled);
        }
        Cv2.ImEncode(".jpg", scaled, out var bytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, llm.JpegQuality)]);
        return bytes;
    }

    private bool BudgetExhausted(int callsNeeded)
    {
        if (llm.MaxCallsPerDay <= 0) return false;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Usage.Snapshot(today).CallsToday + callsNeeded > llm.MaxCallsPerDay;
    }

    /// <summary>Adds the outline to the geometry window and builds the measurement with the median
    /// jar top and bottom. The outline is noisy frame to frame while the jar does not move, so a
    /// single odd outline never rejects a frame; only a sustained shift of the recent outlines
    /// against the older median is a camera move.</summary>
    private LevelMeasurement? Accept(LlmInterpretation i, DateTimeOffset now, out LlmInterpretation result)
    {
        _boxes.Enqueue(i.Jar!.Value);
        while (_boxes.Count > llm.GeometryWindow) _boxes.Dequeue();
        if (CameraMoved())
        {
            var recent = _boxes.TakeLast(llm.SceneChangeFrames).ToList();
            _boxes.Clear();
            foreach (var box in recent) _boxes.Enqueue(box);
            SceneChanged?.Invoke();
        }
        var stable = Median(_boxes);
        result = i;
        if (_boxes.Count < llm.MinGeometryFrames)
        {
            result = i with { Outcome = "geometry_warmup", Reason = "too_few_outlines" };
            return null;
        }
        if (i.SurfaceRow!.Value >= stable.Bottom)
        {
            result = i with { Outcome = "no_surface", Reason = "surface_below_jar_bottom" };
            return null;
        }
        return new LevelMeasurement(now, i.SurfaceRow.Value, stable.Top, stable.Bottom);
    }

    /// <summary>True when the median of the newest <see cref="LlmOptions.SceneChangeFrames"/>
    /// outlines differs from the median of all older ones by more than
    /// <see cref="LlmOptions.GeometryJumpFraction"/> of the frame size on at least two edges, or by
    /// twice that on the bottom edge alone (a vertical move leaves the clamped top and the sides
    /// unchanged). One wandering edge does not count: the model sometimes draws the right wall
    /// onto the shelf or the wall behind the jar, and a false move resets the rise session.</summary>
    private bool CameraMoved()
    {
        var k = llm.SceneChangeFrames;
        if (_boxes.Count < 2 * k) return false;
        var all = _boxes.ToList();
        var recent = Median(all.TakeLast(k));
        var older = Median(all.Take(all.Count - k));
        var reference = _reference!.Value;
        var y = llm.GeometryJumpFraction * reference.Height;
        var x = llm.GeometryJumpFraction * reference.Width;
        var edges = (Math.Abs(recent.Top - older.Top) > y ? 1 : 0)
                    + (Math.Abs(recent.Bottom - older.Bottom) > y ? 1 : 0)
                    + (Math.Abs(recent.Left - older.Left) > x ? 1 : 0)
                    + (Math.Abs(recent.Right - older.Right) > x ? 1 : 0);
        return edges >= 2 || Math.Abs(recent.Bottom - older.Bottom) > 2 * y;
    }

    private static JarBox Median(IEnumerable<JarBox> boxes)
    {
        var list = boxes.ToList();
        double Mid(Func<JarBox, double> pick)
        {
            var sorted = list.Select(pick).Order().ToList();
            var n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        }
        return new JarBox(Mid(b => b.Left), Mid(b => b.Top), Mid(b => b.Right), Mid(b => b.Bottom));
    }

    private LevelMeasurement? Finish(
        Mat frame,
        Rect crop,
        DateTimeOffset now,
        DetectionDiagnostics stats,
        string outcome,
        string? reason,
        LlmInterpretation? interpretation,
        IReadOnlyList<SampleRecord> samples,
        long? latencyMs,
        double? baselineHeightPx,
        double? spread,
        LevelMeasurement? measurement = null)
    {
        LastOutcome = outcome;
        var box = interpretation?.Jar;
        LastDiagnostics = stats with
        {
            FinalRow = interpretation?.SurfaceRow is { } row ? (int)Math.Round(row) : null,
            JarLeftPx = box is { } b ? (int)Math.Round(b.Left) : null,
            JarRightPx = box is { } r ? (int)Math.Round(r.Right) : null,
            JarColumnKind = box is null ? null : "llm",
            SampleCount = samples.Count(s => s.Outcome == "detected"),
            SampleSpreadPx = spread
        };
        if (vision.DebugSaveAnnotatedImages)
        {
            try
            {
                SaveDebug(frame, now, interpretation, measurement, baselineHeightPx);
                AppendJsonl(now, outcome, reason, samples, latencyMs, interpretation?.SurfaceRow, spread);
            }
            catch (IOException ex)
            {
                logger.LogWarning("Could not write debug output: {Message}", ex.Message);
            }
        }
        return measurement;
    }

    /// <summary>One draw of the model for a frame: its interpretation, or the error that replaced it.</summary>
    private async Task<SampleRecord> DrawAsync(
        int index, byte[] jpeg, (int X, int Y, int Width, int Height) crop, CancellationToken ct)
    {
        var request = new LlmRequest(
            llm.Model,
            LlmAnnotationInterpreter.SystemPrompt,
            LlmAnnotationInterpreter.UserPrompt,
            jpeg,
            LlmAnnotationInterpreter.ResponseSchema,
            index == 0 ? 0 : llm.SampleTemperature,
            index);
        LlmCompletion completion;
        try
        {
            completion = await client.CompleteAsync(request, ct);
        }
        catch (LlmException ex)
        {
            logger.LogWarning("LLM call failed: {Message}", ex.Message);
            return new SampleRecord("llm_error", ex.Message, null, null);
        }
        var annotation = LlmAnnotationInterpreter.Parse(completion.Content, out var parseError);
        var interpretation = annotation is null
            ? new LlmInterpretation("llm_rejected", parseError, null, [], [], null)
            : LlmAnnotationInterpreter.Interpret(annotation, crop, llm);
        return new SampleRecord(interpretation.Outcome, interpretation.Reason, interpretation, completion);
    }

    /// <summary>Median surface row and median jar box of the usable draws; the outline and surface
    /// curve of the draw closest to the median row are kept for the debug image.</summary>
    private static LlmInterpretation Consensus(IReadOnlyList<SampleRecord> usable)
    {
        var rows = usable.Select(s => s.Interpretation!.SurfaceRow!.Value).Order().ToList();
        var n = rows.Count;
        var medianRow = n % 2 == 1 ? rows[n / 2] : (rows[n / 2 - 1] + rows[n / 2]) / 2;
        var representative = usable.MinBy(s => Math.Abs(s.Interpretation!.SurfaceRow!.Value - medianRow))!.Interpretation!;
        return representative with
        {
            Jar = Median(usable.Select(s => s.Interpretation!.Jar!.Value)),
            SurfaceRow = medianRow
        };
    }

    /// <summary>Debug image: the jar outline (green), the measured level (red) and the session's
    /// starting level (blue). Level and start line are drawn only inside the glass.</summary>
    private void SaveDebug(
        Mat frame,
        DateTimeOffset now,
        LlmInterpretation? interpretation,
        LevelMeasurement? measurement,
        double? baselineHeightPx)
    {
        using var canvas = frame.Clone();
        if (interpretation is { Outline.Count: > 2 } outline)
        {
            var polygon = outline.Outline
                .Select(p => new OpenCvSharp.Point((int)Math.Round(p.X), (int)Math.Round(p.Y)))
                .ToArray();
            using var lines = new Mat(frame.Size(), MatType.CV_8UC3, Scalar.All(0));
            using var lineMask = new Mat(frame.Size(), MatType.CV_8UC1, Scalar.All(0));
            using var glassMask = new Mat(frame.Size(), MatType.CV_8UC1, Scalar.All(0));
            Cv2.FillPoly(glassMask, [polygon], Scalar.All(255));

            void Horizontal(double row, Scalar color, int thickness)
            {
                var y = (int)Math.Round(row);
                Cv2.Line(lines, new OpenCvSharp.Point(0, y), new OpenCvSharp.Point(frame.Width, y), color, thickness);
                Cv2.Line(lineMask, new OpenCvSharp.Point(0, y), new OpenCvSharp.Point(frame.Width, y),
                    Scalar.All(255), thickness);
            }

            if (interpretation.SurfaceRow is { } level) Horizontal(level, new Scalar(0, 0, 255), 2);
            if (measurement is not null && baselineHeightPx is { } baseline)
                Horizontal(measurement.JarBottomPx - baseline, new Scalar(255, 200, 0), 2);
            using var visible = new Mat();
            Cv2.BitwiseAnd(lineMask, glassMask, visible);
            lines.CopyTo(canvas, visible);
            Cv2.Polylines(canvas, [polygon], true, new Scalar(0, 220, 0), 2);
        }
        Cv2.ImEncode(".jpg", canvas, out var bytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 80)]);
        LatestAnnotatedImageBytes = bytes;
        Directory.CreateDirectory(vision.DebugOutputDirectory);
        var path = Path.Combine(vision.DebugOutputDirectory, $"llm_{now.UtcDateTime:yyyyMMdd_HHmmss}.jpg");
        File.WriteAllBytes(path, bytes);
        LastDebugImagePath = path;
        Prune();
    }

    private void AppendJsonl(
        DateTimeOffset now,
        string outcome,
        string? reason,
        IReadOnlyList<SampleRecord> samples,
        long? latencyMs,
        double? level,
        double? spread)
    {
        var record = new
        {
            time = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            outcome,
            reason,
            model = llm.Model,
            level_row = level,
            sample_spread_px = spread,
            samples_usable = samples.Count(s => s.Outcome == "detected"),
            cost_usd = samples.Sum(s => s.Completion?.CostUsd ?? 0),
            latency_ms = latencyMs,
            samples = samples.Select(s => new
            {
                outcome = s.Outcome,
                reason = s.Reason,
                level_row = s.Interpretation?.SurfaceRow,
                prompt_tokens = s.Completion?.PromptTokens,
                completion_tokens = s.Completion?.CompletionTokens,
                cost_usd = s.Completion?.CostUsd,
                from_cache = s.Completion?.FromCache,
                response = s.Completion?.Content
            })
        };
        Directory.CreateDirectory(vision.DebugOutputDirectory);
        File.AppendAllText(
            Path.Combine(vision.DebugOutputDirectory, $"diagnostics-{now.UtcDateTime:yyyyMMdd}.jsonl"),
            JsonSerializer.Serialize(record) + Environment.NewLine);
    }

    /// <summary>Deletes debug images and diagnostics logs older than the retention window.</summary>
    private void Prune()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(vision.DebugRetentionHours);
        foreach (var file in Directory.EnumerateFiles(vision.DebugOutputDirectory))
        {
            var name = Path.GetFileName(file);
            if (!(name.StartsWith("llm_") || name.StartsWith("diagnostics-"))) continue;
            if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
    }
}

/// <summary>One draw of the model for a frame; <see cref="Completion"/> is null when the call failed.</summary>
public sealed record SampleRecord(
    string Outcome, string? Reason, LlmInterpretation? Interpretation, LlmCompletion? Completion);

/// <summary>Running statistics of the max-min spread (px) between the samples of a cycle.</summary>
public sealed class SampleSpreadStats
{
    private double _sum;

    public int Cycles { get; private set; }

    /// <summary>Cycles whose spread exceeded the warn threshold.</summary>
    public int Noisy { get; private set; }

    public double Max { get; private set; }

    public double Mean => Cycles == 0 ? 0 : _sum / Cycles;

    public void Observe(double spreadPx, double warnPx)
    {
        Cycles++;
        _sum += spreadPx;
        Max = Math.Max(Max, spreadPx);
        if (spreadPx > warnPx) Noisy++;
    }
}
