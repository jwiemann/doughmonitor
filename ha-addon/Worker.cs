using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Mqtt;
using SourdoughMonitor.Services;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor;

public sealed class Worker(
    FrigateSnapshotClient frigate,
    JarLevelDetector detector,
    RiseAnalyzer analyzer,
    HaMqttPublisher mqtt,
    MonitorOptions options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        mqtt.ResetRequested += async () =>
        {
            logger.LogInformation("Manual session reset via MQTT");
            var reading = analyzer.Reset();
            await mqtt.PublishReadingAsync(reading, ct);
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

    private async Task SampleOnceAsync(CancellationToken ct)
    {
        var (jpeg, measurement) = await CaptureWithRetriesAsync(ct);
        if (jpeg is null)
        {
            logger.LogWarning("No snapshot from Frigate");
            return;
        }

        // Publish debug image and diagnostics via MQTT when debug mode is enabled
        if (options.Mqtt.DebugMode && detector.LatestAnnotatedImageBytes is not null)
        {
            await mqtt.PublishDebugImageAsync(detector.LatestAnnotatedImageBytes, ct);
            await mqtt.PublishDetectionDiagnosticsAsync(detector.LastDiagnostics, measurement, ct);
        }
        if (measurement is null)
        {
            await mqtt.PublishUnavailableMeasurementAsync(ct);
            return;
        }
        var reading = analyzer.Analyze(measurement);
        if (reading is null)
        {
            logger.LogWarning("Rejected implausible dough-height jump; treating cycle as unavailable");
            await mqtt.PublishUnavailableMeasurementAsync(ct);
            return;
        }
        await mqtt.PublishReadingAsync(reading, ct);
        logger.LogInformation(
            "Rise {Rise}% | Rate {Rate}%/h | ETA {Eta} | Peaked {Peaked}",
            reading.RisePercent,
            reading.RiseRatePercentPerHour,
            reading.PredictedPeakTime,
            reading.Peaked);
    }

    /// <summary>Fetches a snapshot and runs detection, retrying a few times within this
    /// cycle on transient failure (bad frame, camera hiccup) so a single flaky attempt
    /// doesn't drop a whole sampling interval's worth of data.</summary>
    private async Task<(byte[]? Jpeg, LevelMeasurement? Measurement)> CaptureWithRetriesAsync(CancellationToken ct)
    {
        byte[]? jpeg = null;
        LevelMeasurement? measurement = null;
        for (var attempt = 0; attempt <= options.Frigate.SnapshotRetryCount; attempt++)
        {
            jpeg = await frigate.GetLatestSnapshotAsync(ct);
            if (jpeg is not null)
            {
                measurement = detector.Measure(jpeg, DateTimeOffset.Now);
                if (measurement is not null && options.Vision.RoiY is not null)
                {
                    measurement = JarLevelDetector.AdjustMeasurementForRoi(measurement, options.Vision.RoiY.Value);
                }
                if (measurement is not null) break;
            }
            if (attempt < options.Frigate.SnapshotRetryCount)
            {
                logger.LogWarning(
                    "Snapshot/detection attempt {Attempt} failed; retrying in {Delay}s",
                    attempt + 1,
                    options.Frigate.SnapshotRetryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(options.Frigate.SnapshotRetryDelaySeconds), ct);
            }
        }
        return (jpeg, measurement);
    }
}
