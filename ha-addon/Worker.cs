using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Mqtt;
using SourdoughMonitor.Services;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor;

public sealed class Worker(
    FrigateSnapshotClient frigate,
    SwitchableDetector detector,
    LlmJarDetector llmDetector,
    RiseAnalyzer analyzer,
    HaMqttPublisher mqtt,
    MonitorOptions options,
    ILogger<Worker> logger) : BackgroundService
{
    private RiseReading? _lastReading;

    private double _silentMinutes;

    private bool _stalePublished;

    private readonly SurfaceLossMonitor _surfaceLoss = new(options.Analysis);

    /// <summary>Wall-contact height next to the classic reading; published with the debug diagnostics only.</summary>
    private readonly WallHeightService _wallHeight = new();

    private bool _lossWarned;

    /// <summary>Set while the detector replays recent archive frames next to a restored session: a
    /// scene change found in the past must not reset the session that was just restored.</summary>
    private bool _warmingUpRestoredSession;

    private volatile bool _backfilling;
    private bool? _llmRequestedDuringBackfill;

    /// <summary>"Bread mode" gates the oven-preheat signal: feeding the daily starter must not warn about an oven.
    /// It is persisted so a restart keeps the choice; the default is off.</summary>
    private bool LoadBreadMode()
    {
        var path = options.Analysis.BreadModeFilePath;
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                   && File.ReadAllText(path).Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SaveBreadMode(bool enabled)
    {
        var path = options.Analysis.BreadModeFilePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.WriteAllText(path, enabled ? "on" : "off");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not persist the bread mode");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        mqtt.ResetRequested += async () =>
        {
            logger.LogInformation("Manual session reset via MQTT");
            var reading = analyzer.Reset();
            await PublishSessionEndAsync(ct);
            await TrackPublishedAsync(reading, ct);
        };
        mqtt.LlmDetectionRequested += async enabled =>
        {
            // The startup backfill runs on the classic detector; a request made meanwhile is applied when it ends
            // (restoring the earlier choice afterwards would silently drop it, as on 2026-11-12).
            if (_backfilling) _llmRequestedDuringBackfill = enabled;
            else
            {
                if (detector.LlmEnabled != enabled)
                    logger.LogInformation("Detection switched to {Detector} via MQTT", enabled ? "vision LLM" : "classic image analysis");
                detector.LlmEnabled = enabled;
            }
            await mqtt.PublishLlmDetectionStateAsync(enabled, ct);
        };
        mqtt.BreadModeRequested += async enabled =>
        {
            logger.LogInformation("Bread mode {State} via MQTT", enabled ? "on" : "off");
            mqtt.BreadMode = enabled;
            analyzer.BreadMode = enabled;
            SaveBreadMode(enabled);
            await mqtt.PublishBreadModeStateAsync(enabled, ct);
            // Apply at once instead of with the next reading, so switching off cancels the signal immediately.
            if (_lastReading is not null) await mqtt.PublishReadingAsync(_lastReading, ct);
        };
        mqtt.BreadMode = LoadBreadMode();
        analyzer.BreadMode = mqtt.BreadMode;
        // Geometry changes invalidate the pixel-height baseline. The detector verifies
        // the new jar/base/surface; lighting and sampling-resolution changes retain it.
        detector.SceneChanged += () =>
        {
            if (_warmingUpRestoredSession) return;
            logger.LogWarning("Jar geometry changed (camera moved/bumped); resetting rise session");
            _ = analyzer.Reset();
            _wallHeight.Reset();
        };
        try
        {
            await mqtt.ConnectAsync(ct)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            logger.LogWarning("MQTT connect timed out; continuing without MQTT updates");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT connect failed; continuing without MQTT updates");
        }
        try
        {
            await mqtt.PublishLlmDetectionStateAsync(detector.LlmEnabled, ct);
            await mqtt.PublishBreadModeStateAsync(mqtt.BreadMode, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish the LLM detection switch state");
        }
        await BackfillSessionAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Frigate.SampleIntervalMinutes));
        do
        {
            try
            {
                await SampleOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sampling cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Replays archived frames through the classic detector and the analyzer before live
    /// sampling starts. Without a restored session (first start of this version, a lost state file)
    /// the whole session is rebuilt from the last <see cref="AnalysisOptions.MaxSessionHours"/>.
    /// With a restored session only the last hours are replayed: the detector keeps no state across
    /// restarts (last surface, dough floor, jar columns), and without it the evening frames after a
    /// restart find no surface at all (2026-11-09 15:40: method "none", no lines in the debug image);
    /// the analyzer ignores frames it has already seen and takes those from the downtime. Classic
    /// detection only: replaying frames through the paid vision model would cost a day's budget.
    /// Best effort; failure leaves the live state as it is.</summary>
    private async Task BackfillSessionAsync(CancellationToken ct)
    {
        var configured = options.Frigate.SnapshotArchiveDirectory;
        if (string.IsNullOrWhiteSpace(configured)) return;
        var restored = analyzer.BaselineDoughHeightPx is not null;
        var directory = Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        var llmWasEnabled = detector.LlmEnabled;
        try
        {
            _backfilling = true;
            _llmRequestedDuringBackfill = null;
            detector.LlmEnabled = false;
            _warmingUpRestoredSession = restored;
            var started = DateTimeOffset.UtcNow;
            var result = await ArchiveBackfill.RunAsync(
                directory, detector, analyzer, started,
                restored ? TimeSpan.FromHours(6) : TimeSpan.FromHours(options.Analysis.MaxSessionHours), ct);
            logger.LogWarning(
                "{What} from the snapshot archive: {Frames} frames, {Measured} measured, {Seconds:F0} s; "
                + "session start {Start:u} ({Reason})",
                restored ? "Detector warmed up" : "Session rebuilt", result.Frames, result.Measured,
                (DateTimeOffset.UtcNow - started).TotalSeconds,
                result.LastReading?.SessionStart, analyzer.LastSessionStartReason ?? "none");
            if (result.LastReading is { } reading) await TrackPublishedAsync(reading, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not replay the snapshot archive; continuing with the live state");
        }
        finally
        {
            _warmingUpRestoredSession = false;
            _backfilling = false;
            detector.LlmEnabled = _llmRequestedDuringBackfill ?? llmWasEnabled;
            _llmRequestedDuringBackfill = null;
        }
    }

    private async Task SampleOnceAsync(CancellationToken ct)
    {
        var (jpeg, measurement) = await CaptureWithRetriesAsync(ct);
        if (jpeg is null)
        {
            logger.LogWarning("No snapshot from Frigate");
            await MarkSilentAsync(ct);
            return;
        }
        if (detector.LastOutcome == "empty_scene") analyzer.NoteEmptyScene(DateTimeOffset.UtcNow);

        try
        {
            await mqtt.PublishLlmUsageAsync(
                llmDetector.Usage.Snapshot(DateOnly.FromDateTime(DateTime.UtcNow)),
                detector.LlmEnabled ? options.Llm.Model : "classic", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish LLM usage");
        }

        var lossMinutes = _surfaceLoss.Observe(
            detector.LastOutcome, _lastReading?.Peaked == true, DateTimeOffset.UtcNow);
        if (lossMinutes is null)
        {
            _lossWarned = false;
        }
        else if (!_lossWarned)
        {
            _lossWarned = true;
            logger.LogWarning(
                "No dough surface for {Minutes:F0} min after a confirmed peak; the detector may have lost the surface (collapse?)",
                lossMinutes);
        }

        WallHeightReading? wall = null;
        try
        {
            wall = _wallHeight.Measure(jpeg, detector.LastDiagnostics, measurement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Wall-contact height failed; continuing without it");
        }
        if (wall is not null)
        {
            logger.LogInformation(
                "Height rows: dome {Dome} ({DomeSource}) | wall {Wall} | steady {Steady} ({Source})",
                wall.DomeTopRow, wall.DomeSource, wall.WallLevelRow, wall.StableHeightRow, wall.HeightSource);
        }

        // Publish debug image and diagnostics via MQTT when debug mode is enabled
        if (options.Mqtt.DebugMode && detector.LatestAnnotatedImageBytes is not null)
        {
            await mqtt.PublishDebugImageAsync(detector.LatestAnnotatedImageBytes, ct);
            await mqtt.PublishDetectionDiagnosticsAsync(detector.LastDiagnostics, measurement, lossMinutes, ct, wall);
        }
        if (measurement is null)
        {
            await mqtt.PublishUnavailableMeasurementAsync(ct);
            await MarkSilentAsync(ct);
            return;
        }
        var reading = analyzer.Analyze(measurement);
        if (reading is null)
        {
            logger.LogWarning(
                "Measurement rejected (implausible dough-height jump, pending feeding confirmation, or dough floor not re-measured yet); treating cycle as unavailable");
            await mqtt.PublishUnavailableMeasurementAsync(ct);
            await MarkSilentAsync(ct);
            return;
        }
        // A reading that starts a new session has just ended the previous one: publish its
        // summary (max rise, measured peak, prediction error) before the fresh session.
        if (reading.NewSession)
            logger.LogWarning("New rise session started: {Reason}", analyzer.LastSessionStartReason ?? "unknown");
        await PublishSessionEndAsync(ct);
        await TrackPublishedAsync(reading, ct);
        logger.LogInformation(
            "Rise {Rise}% | Rate {Rate}%/h | ETA {Eta} | Peaked {Peaked}",
            reading.RisePercent,
            reading.RiseRatePercentPerHour,
            reading.PredictedPeakTime,
            reading.Peaked);
    }

    /// <summary>Records a fresh reading and publishes it; resets the measurement-outage
    /// bookkeeping so a later outage starts counting from now.</summary>
    private async Task TrackPublishedAsync(RiseReading reading, CancellationToken ct)
    {
        _lastReading = reading;
        _silentMinutes = 0;
        _stalePublished = false;
        await mqtt.PublishReadingAsync(reading, ct);
    }

    /// <summary>Publishes the summary of a session the analyzer just closed, if any.</summary>
    private async Task PublishSessionEndAsync(CancellationToken ct)
    {
        if (analyzer.DequeueSessionSummary() is { } summary)
            await mqtt.PublishSessionSummaryAsync(summary, ct);
    }

    /// <summary>Counts a cycle without a usable measurement. Once the outage exceeds the
    /// configured threshold the retained reading is declared stale exactly once, so old
    /// values (especially the peak ETA) cannot look current during a detection gap.</summary>
    private async Task MarkSilentAsync(CancellationToken ct)
    {
        _silentMinutes += options.Frigate.SampleIntervalMinutes;
        if (_stalePublished || _lastReading is null || _silentMinutes < options.Mqtt.StaleAfterMinutes)
            return;
        _stalePublished = true;
        logger.LogWarning(
            "No usable measurement for {Minutes} min; marking the published reading stale",
            _silentMinutes);
        await mqtt.PublishStaleAsync(_lastReading, ct);
    }

    /// <summary>Fetches a snapshot and runs detection, retrying a few times within this
    /// cycle on transient failure (camera hiccup) so a single flaky attempt doesn't drop a
    /// whole sampling interval's worth of data. Retries apply ONLY to failed fetches: a
    /// re-fetch wakes battery cameras again (their live stream is what breaks under
    /// constant polling), and a detection miss is re-sampled by the next cycle anyway.</summary>
    private async Task<(byte[]? Jpeg, LevelMeasurement? Measurement)> CaptureWithRetriesAsync(CancellationToken ct)
    {
        // Read once per cycle: it reflects the session as it stood before this cycle's
        // measurement, which is exactly the "session start" the debug image should mark.
        var sessionBaselineHeightPx = analyzer.BaselineDoughHeightPx;
        byte[]? jpeg = null;
        LevelMeasurement? measurement = null;
        for (var attempt = 0; attempt <= options.Frigate.SnapshotRetryCount; attempt++)
        {
            jpeg = await frigate.GetLatestSnapshotAsync(ct);
            if (jpeg is null)
            {
                if (attempt < options.Frigate.SnapshotRetryCount)
                {
                    logger.LogWarning(
                        "Snapshot fetch attempt {Attempt} failed; retrying in {Delay}s",
                        attempt + 1,
                        options.Frigate.SnapshotRetryDelaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(options.Frigate.SnapshotRetryDelaySeconds), ct);
                    continue;
                }
                break;
            }
            measurement = await detector.MeasureAsync(jpeg, DateTimeOffset.Now, sessionBaselineHeightPx, ct);
            if (measurement is null)
            {
                logger.LogInformation("Snapshot fetched but dough not detected; waiting for next cycle");
            }
            break;
        }
        return (jpeg, measurement);
    }
}
