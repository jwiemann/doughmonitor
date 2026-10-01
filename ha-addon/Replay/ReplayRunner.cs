using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Replay;

/// <summary>Offline batch analysis of a folder of exported camera snapshots.
/// Replays every frame through the live detector and analyzer (the same code path as the
/// add-on's sampling cycle), persists per-frame measurements, diagnostics and annotated
/// debug images, and reports the peak prediction derived from the rising rate.
/// Usage: dotnet run -- replay &lt;folder&gt; [--out dir] [--config path] [--roi x,y,w,h]</summary>
public static class ReplayRunner
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        string? folder = null, outDir = null, configArg = null, roiArg = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    configArg = args[++i];
                    break;
                case "--roi" when i + 1 < args.Length:
                    roiArg = args[++i];
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        PrintUsage($"Unknown or incomplete option: {args[i]}");
                        return 2;
                    }
                    if (folder is not null)
                    {
                        PrintUsage("Multiple input folders given.");
                        return 2;
                    }
                    folder = args[i];
                    break;
            }
        }
        if (folder is null || !Directory.Exists(folder))
        {
            PrintUsage(folder is null ? "Missing input folder." : $"Folder not found: {folder}");
            return 2;
        }

        var configPath = ResolveConfigPath(configArg);
        var options = LoadOptions(configPath);
        var roi = ParseRoi(roiArg);
        var outPath = outDir ?? "replay-out";
        Directory.CreateDirectory(outPath);
        var visionOptions = BuildReplayVisionOptions(options.Vision, outPath, roi);
        Directory.CreateDirectory(visionOptions.DebugOutputDirectory);

        var frames = CollectFrames(folder);
        var mtimeFallback = frames.Count(f => !f.FromFilename);
        Console.WriteLine($"Replay: {frames.Count} frames from {frames[0].Time:yyyy-MM-dd HH:mm} to {frames[^1].Time:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"Output: {Path.GetFullPath(outPath)}");

        var detector = new JarLevelDetector(visionOptions);
        var riseAnalyzer = new RiseAnalyzer(BuildReplayAnalysisOptions(options.Analysis));

        var rows = new List<ReplayFrameRow>(frames.Count);
        var warnings = new List<string>();
        RiseReading? lastReading = null;

        foreach (var frame in frames)
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(frame.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Unreadable file: {frame.Path} ({ex.Message})");
                rows.Add(DecodeFailedRow(frame));
                continue;
            }
            // Session baseline mirrors the live Worker: the analyzer's baseline as it stood
            // before this frame, so debug images draw the session-start line like the addon.
            var measurement = detector.Measure(bytes, frame.Time, riseAnalyzer.BaselineDoughHeightPx);
            var diagnostics = detector.LastDiagnostics;
            var debugImage = detector.LastDebugImagePath is { } path
                ? Path.GetRelativePath(Path.GetFullPath(outPath), path)
                : null;
            var reading = measurement is null ? null : riseAnalyzer.Analyze(measurement);
            if (reading is not null)
            {
                lastReading = reading;
            }
            rows.Add(new ReplayFrameRow(
                File: frame.Path,
                Time: frame.Time,
                TimeSource: frame.FromFilename ? "filename" : "mtime",
                Outcome: detector.LastOutcome,
                Method: diagnostics?.Method,
                FrameMean: diagnostics?.FrameMean,
                FrameMedian: diagnostics?.FrameMedian,
                FrameP10: diagnostics?.FrameP10,
                FrameP90: diagnostics?.FrameP90,
                BandContrast: diagnostics?.BandContrast,
                BandTopRow: diagnostics?.BandTopRow,
                FinalRow: diagnostics?.FinalRow,
                DoughTopPx: measurement?.DoughTopPx,
                JarTopPx: measurement?.JarTopPx,
                JarBottomPx: measurement?.JarBottomPx,
                DoughHeightPx: measurement?.DoughHeightPx,
                Reading: measurement is null ? "" : reading is null ? "unavailable" : "ok",
                RisePercent: reading?.RisePercent,
                RiseRatePctPerHour: reading?.RiseRatePercentPerHour,
                PredictedPeakPercent: reading?.PredictedPeakPercent,
                PredictedPeakTime: reading?.PredictedPeakTime,
                Peaked: reading?.Peaked ?? false,
                NewSession: reading?.NewSession ?? false,
                DebugImage: debugImage));
        }

        var methodCounts = rows
            .Where(r => r.Method is not null)
            .GroupBy(r => r.Method!)
            .ToDictionary(g => g.Key, g => g.Count());
        var summary = new ReplaySummary(
            Path.GetFullPath(folder),
            configPath,
            roi,
            rows.Count,
            rows.Count(r => r.Outcome == "detected"),
            rows.Count(r => r.Outcome == "dark_frame"),
            rows.Count(r => r.Outcome is "no_surface" or "no_jar"),
            rows.Count(r => r.Outcome == "decode_failed"),
            rows.Count(r => r.Reading == "unavailable"),
            mtimeFallback,
            rows[0].Time,
            rows[^1].Time,
            methodCounts,
            lastReading,
            warnings);

        WriteCsv(Path.Combine(outPath, "readings.csv"), rows);
        await File.WriteAllTextAsync(
            Path.Combine(outPath, "summary.json"),
            JsonSerializer.Serialize(summary, JsonOpts));
        ReplayReportWriter.WriteHtml(Path.Combine(outPath, "report.html"), summary, rows);

        PrintSummary(summary);
        return 0;
    }

    private static ReplayFrameRow DecodeFailedRow(Frame frame) => new(
        File: frame.Path,
        Time: frame.Time,
        TimeSource: frame.FromFilename ? "filename" : "mtime",
        Outcome: "decode_failed",
        Method: null,
        FrameMean: null,
        FrameMedian: null,
        FrameP10: null,
        FrameP90: null,
        BandContrast: null,
        BandTopRow: null,
        FinalRow: null,
        DoughTopPx: null,
        JarTopPx: null,
        JarBottomPx: null,
        DoughHeightPx: null,
        Reading: "",
        RisePercent: null,
        RiseRatePctPerHour: null,
        PredictedPeakPercent: null,
        PredictedPeakTime: null,
        Peaked: false,
        NewSession: false,
        DebugImage: null);

    private static List<Frame> CollectFrames(string folder)
    {
        var frames = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(path => SnapshotTimestampResolver.TryParse(path, out var time)
                ? new Frame(path, time, true)
                : new Frame(
                    path,
                    new DateTimeOffset(File.GetLastWriteTimeUtc(path).ToLocalTime()),
                    false))
            .OrderBy(f => f.Time)
            .ThenBy(f => f.Path)
            .ToList();
        if (frames.Count == 0)
        {
            throw new ArgumentException(
                $"No JPEG/PNG snapshots found under: {folder}");
        }
        return frames;
    }

    private static void WriteCsv(string path, IReadOnlyList<ReplayFrameRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "file,time,time_source,outcome,method,frame_mean,frame_median,frame_p10,frame_p90,band_contrast,"
            + "band_top_row,final_row,dough_top_px,jar_top_px,jar_bottom_px,dough_height_px,"
            + "reading,rise_percent,rise_rate_pct_per_h,predicted_peak_percent,"
            + "predicted_peak_time,peaked,new_session,debug_image");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(
                ",",
                Csv(r.File),
                r.Time.ToString("O", CultureInfo.InvariantCulture),
                r.TimeSource,
                r.Outcome,
                r.Method ?? "",
                N(r.FrameMean),
                N(r.FrameMedian),
                N(r.FrameP10),
                N(r.FrameP90),
                N(r.BandContrast),
                r.BandTopRow?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.FinalRow?.ToString(CultureInfo.InvariantCulture) ?? "",
                N(r.DoughTopPx),
                N(r.JarTopPx),
                N(r.JarBottomPx),
                N(r.DoughHeightPx),
                r.Reading,
                N(r.RisePercent),
                N(r.RiseRatePctPerHour),
                N(r.PredictedPeakPercent),
                r.PredictedPeakTime?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                r.Peaked ? "true" : "false",
                r.NewSession ? "true" : "false",
                r.DebugImage ?? ""));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void PrintSummary(ReplaySummary s)
    {
        Console.WriteLine();
        Console.WriteLine($"Frames: {s.FrameCount} over {s.LastTime - s.FirstTime}");
        Console.WriteLine(
            $"  detected {s.Detected} | dark {s.DarkFrames} | no_surface {s.NoSurface} "
            + $"| decode_failed {s.DecodeFailed} | readings unavailable {s.ReadingsUnavailable} "
            + $"| mtime fallback {s.MtimeFallback}");
        Console.WriteLine(
            $"  methods: {string.Join(", ", s.MethodCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (s.LastRiseReading is { } r)
        {
            Console.WriteLine(
                $"Rise: {r.RisePercent:F1}% | rate {r.RiseRatePercentPerHour?.ToString("F1") ?? "-"}%/h"
                + $" | predicted peak {r.PredictedPeakPercent?.ToString("F0") ?? "-"}%"
                + $" at {r.PredictedPeakTime?.ToString("yyyy-MM-dd HH:mm") ?? "-"} | peaked {r.Peaked}");
        }
        foreach (var warning in s.Warnings)
        {
            Console.WriteLine($"Warning: {warning}");
        }
    }

    private static string N(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Csv(string value) =>
        value.Contains(',') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static void PrintUsage(string error)
    {
        Console.Error.WriteLine($"Error: {error}");
        Console.Error.WriteLine("Usage: dotnet run -- replay <folder> [--out dir] [--config appsettings.json] [--roi x,y,w,h]");
    }

    private static string? ResolveConfigPath(string? configArg)
    {
        if (configArg is not null)
        {
            if (!File.Exists(configArg))
            {
                throw new FileNotFoundException($"Config file not found: {configArg}", configArg);
            }
            return configArg;
        }
        var cwdConfig = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        if (File.Exists(cwdConfig)) return cwdConfig;
        var baseConfig = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        return File.Exists(baseConfig) ? baseConfig : null;
    }

    private static MonitorOptions LoadOptions(string? configPath)
    {
        var builder = new ConfigurationBuilder();
        if (configPath is not null)
        {
            builder.AddJsonFile(configPath, optional: false);
        }
        builder.AddEnvironmentVariables();
        return builder.Build()
            .GetSection("Monitor")
            .Get<MonitorOptions>() ?? new MonitorOptions
        {
            Frigate = new FrigateOptions { Camera = "replay" },
            Mqtt = new MqttOptions()
        };
    }

    private static (int X, int Y, int W, int H)? ParseRoi(string? roiArg)
    {
        if (roiArg is null) return null;
        var parts = roiArg.Split(',');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            throw new ArgumentException($"--roi expects x,y,w,h integers, got: {roiArg}");
        }
        return (x, y, w, h);
    }

    private static VisionOptions BuildReplayVisionOptions(
        VisionOptions src,
        string outDir,
        (int X, int Y, int W, int H)? roi)
    {
        var debugDir = Path.Combine(Path.GetFullPath(outDir), "debug");
        return new VisionOptions
        {
            RoiX = roi?.X ?? src.RoiX,
            RoiY = roi?.Y ?? src.RoiY,
            RoiWidth = roi?.W ?? src.RoiWidth,
            RoiHeight = roi?.H ?? src.RoiHeight,
            MinJarWallFraction = src.MinJarWallFraction,
            MinJarWidthFraction = src.MinJarWidthFraction,
            MinFrameIntensity = src.MinFrameIntensity,
            MinFrameContrast = src.MinFrameContrast,
            DebugSaveAnnotatedImages = true,
            DebugOutputDirectory = debugDir
        };
    }

    private static AnalysisOptions BuildReplayAnalysisOptions(AnalysisOptions src) => new()
    {
        SlopeWindowMinutes = src.SlopeWindowMinutes,
        ResetDropFraction = src.ResetDropFraction,
        MinSamplesForFit = src.MinSamplesForFit,
        MaxEtaRelativeStdError = src.MaxEtaRelativeStdError,
        PeakConfirmWindows = src.PeakConfirmWindows,
        MaxSessionHours = src.MaxSessionHours,
        MedianWindowSize = src.MedianWindowSize,
        PeakFraction = src.PeakFraction,
        FlatSlopePercentPerHour = src.FlatSlopePercentPerHour,
        MinRisePercentForPeak = src.MinRisePercentForPeak,
        MaxRisePxPerMinute = src.MaxRisePxPerMinute,
        JitterTolerancePx = src.JitterTolerancePx,
        MaxImplausibleJumpRejects = src.MaxImplausibleJumpRejects,
        CollapseConfirmSamples = src.CollapseConfirmSamples,
        // Replay must never touch the live addon session state file.
        StateFilePath = null
    };

    private sealed record Frame(string Path, DateTimeOffset Time, bool FromFilename);
}
