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
    GrowthTracker growthTracker,
    HaMqttPublisher mqtt,
    MonitorOptions options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        mqtt.ResetRequested += () =>
        {
            logger.LogInformation("Manual session reset via MQTT");
            analyzer.Reset();
            return Task.CompletedTask;
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
        var jpeg = await frigate.GetLatestSnapshotAsync(ct);
        if (jpeg is null)
        {
            logger.LogWarning("No snapshot from Frigate");
            return;
        }
        var measurement = detector.Measure(jpeg, DateTimeOffset.Now);
        if (measurement is not null && options.Vision.RoiY is not null)
        {
            measurement = JarLevelDetector.AdjustMeasurementForRoi(measurement, options.Vision.RoiY.Value);
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
        growthTracker.Add(measurement);
        _ = growthTracker.Analyze();
        var reading = analyzer.Analyze(measurement);
        await mqtt.PublishReadingAsync(reading, ct);
        logger.LogInformation(
            "Rise {Rise}% | Rate {Rate}%/h | ETA {Eta} | Peaked {Peaked}",
            reading.RisePercent,
            reading.RiseRatePercentPerHour,
            reading.PredictedPeakTime,
            reading.Peaked);
    }
}
