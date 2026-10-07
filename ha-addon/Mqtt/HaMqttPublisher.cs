using System.Text.Json;

using MQTTnet;
using MQTTnet.Client;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Mqtt;

/// <summary>Publishes readings and HA MQTT discovery config; listens for a reset command.</summary>
public sealed class HaMqttPublisher(MqttOptions options) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();

    private string StateTopic => $"{options.DeviceId}/state";

    private string AvailabilityTopic => $"{options.DeviceId}/availability";

    public string ResetCommandTopic => $"{options.DeviceId}/cmd/reset";

    private string DebugImageTopic => $"{options.DeviceId}/debug_image";

    private string DiagnosticsTopic => $"{options.DeviceId}/diagnostics";

    private string SessionSummaryTopic => $"{options.DeviceId}/session_summary";

    public event Func<Task>? ResetRequested;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder().WithTcpServer(options.Host, options.Port)
            .WithClientId(options.DeviceId)
            .WithWillTopic(AvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true);
        if (!string.IsNullOrEmpty(options.Username))
            builder = builder.WithCredentials(options.Username, options.Password);
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            if (e.ApplicationMessage.Topic == ResetCommandTopic && ResetRequested is not null)
                await ResetRequested();
        };
        await _client.ConnectAsync(builder.Build(), ct);
        await _client.SubscribeAsync(ResetCommandTopic, cancellationToken: ct);
        await PublishAsync(AvailabilityTopic, "online", retain: true, ct);
        await PublishDiscoveryAsync(ct);
    }

    public async Task PublishReadingAsync(RiseReading reading, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                rise_percent = reading.RisePercent,
                rise_rate = reading.RiseRatePercentPerHour,
                predicted_peak_percent = reading.PredictedPeakPercent,
                peak_eta = reading.PredictedPeakTime?.ToString("O"),
                peak_eta_low = reading.PredictedPeakTimeLow?.ToString("O"),
                peak_eta_high = reading.PredictedPeakTimeHigh?.ToString("O"),
                peaked = reading.Peaked ? "ON" : "OFF",
                new_session = reading.NewSession,
                data_stale = "OFF",
                last_update = reading.Time.ToString("O"),
                session_start = reading.SessionStart?.ToString("O")
            },
            JsonOpts);
        await PublishAsync(StateTopic, payload, retain: true, ct);
    }

    /// <summary>Marks the published reading as stale after a measurement outage: the
    /// measured values are cleared while the time of the last real reading is kept, so a
    /// retained peak ETA or rise percentage can never be mistaken for a current one.</summary>
    public async Task PublishStaleAsync(RiseReading lastReading, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                rise_percent = (double?)null,
                rise_rate = (double?)null,
                predicted_peak_percent = (double?)null,
                peak_eta = (string?)null,
                peak_eta_low = (string?)null,
                peak_eta_high = (string?)null,
                peaked = "OFF",
                new_session = false,
                data_stale = "ON",
                last_update = lastReading.Time.ToString("O"),
                session_start = lastReading.SessionStart?.ToString("O")
            },
            JsonOpts);
        await PublishAsync(StateTopic, payload, retain: true, ct);
    }

    /// <summary>Publishes the summary of a session that just ended, including the
    /// prediction error (predicted peak time minus the measured maximum) for review.</summary>
    public async Task PublishSessionSummaryAsync(SessionSummary summary, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                session_start = summary.SessionStart.ToString("O"),
                session_end = summary.SessionEnd.ToString("O"),
                max_rise_percent = summary.MaxRisePercent,
                max_rise_time = summary.MaxRiseTime.ToString("O"),
                max_rate = summary.MaxRatePercentPerHour,
                predicted_peak_time = summary.PredictedPeakTime?.ToString("O"),
                prediction_error_minutes = summary.PredictionErrorMinutes
            },
            JsonOpts);
        await PublishAsync(SessionSummaryTopic, payload, retain: true, ct);
    }

    public Task PublishUnavailableMeasurementAsync(CancellationToken ct) =>
        PublishAsync($"{options.DeviceId}/measurement_status", "no_reading", retain: false, ct);

    public async Task PublishDebugImageAsync(byte[] jpegBytes, CancellationToken ct)
    {
        if (!options.DebugMode || jpegBytes.Length == 0) return;
        await PublishRawAsync(DebugImageTopic, jpegBytes, retain: false, ct);
    }

    public async Task PublishDetectionDiagnosticsAsync(
        DetectionDiagnostics? diagnostics,
        LevelMeasurement? measurement,
        CancellationToken ct)
    {
        if (!options.DebugMode) return;
        var payload = JsonSerializer.Serialize(
            new
            {
                method = diagnostics?.Method ?? "none",
                band_contrast = (int)Math.Round(diagnostics?.BandContrast ?? 0),
                band_top_row = diagnostics?.BandTopRow,
                final_row = diagnostics?.FinalRow,
                frame_mean = diagnostics?.FrameMean,
                frame_median = diagnostics?.FrameMedian,
                frame_p10 = diagnostics?.FrameP10,
                frame_p90 = diagnostics?.FrameP90,
                dough_top_px = measurement?.DoughTopPx,
                jar_bottom_px = measurement?.JarBottomPx,
                jar_height_px =
                    measurement is not null ? (double?)(measurement.JarBottomPx - measurement.JarTopPx) : null,
                dough_height_px = measurement?.DoughHeightPx,
                dough_floor_px = measurement?.DoughFloorPx
            },
            JsonOpts);
        await PublishAsync(DiagnosticsTopic, payload, retain: true, ct);
    }

    private async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        var device = new
        {
            identifiers = new[] { options.DeviceId },
            name = "Sourdough Monitor",
            manufacturer = "DIY",
            model = "JarWatch 1.0"
        };
        await PublishSensorConfigAsync(
            "rise_percent",
            "Starter Rise",
            "%",
            "{{ value_json.rise_percent }}",
            device,
            ct,
            stateTopicOverride: StateTopic);
        await PublishSensorConfigAsync(
            "rise_rate",
            "Starter Rise Rate",
            "%/h",
            "{{ value_json.rise_rate }}",
            device,
            ct,
            stateTopicOverride: StateTopic);
        await PublishSensorConfigAsync(
            "predicted_peak_percent",
            "Predicted Peak Rise",
            "%",
            "{{ value_json.predicted_peak_percent }}",
            device,
            ct,
            stateTopicOverride: StateTopic);
        await PublishSensorConfigAsync(
            "peak_eta",
            "Peak ETA",
            null,
            "{{ value_json.peak_eta }}",
            device,
            ct,
            deviceClass: "timestamp",
            stateTopicOverride: StateTopic);
        await PublishSensorConfigAsync(
            "peak_eta_low",
            "Peak ETA Earliest",
            null,
            "{{ value_json.peak_eta_low }}",
            device,
            ct,
            deviceClass: "timestamp",
            stateTopicOverride: StateTopic);
        await PublishSensorConfigAsync(
            "peak_eta_high",
            "Peak ETA Latest",
            null,
            "{{ value_json.peak_eta_high }}",
            device,
            ct,
            deviceClass: "timestamp",
            stateTopicOverride: StateTopic);
        await PublishSensorConfigAsync(
            "last_update",
            "Last Reading",
            null,
            "{{ value_json.last_update }}",
            device,
            ct,
            deviceClass: "timestamp",
            entityCategory: "diagnostic",
            stateTopicOverride: StateTopic);
        await PublishSensorConfigAsync(
            "session_start",
            "Session Started",
            null,
            "{{ value_json.session_start }}",
            device,
            ct,
            deviceClass: "timestamp",
            stateTopicOverride: StateTopic);
        var binaryConfig = JsonSerializer.Serialize(
            new
            {
                name = "Starter Peaked",
                unique_id = $"{options.DeviceId}_peaked",
                state_topic = StateTopic,
                value_template = "{{ value_json.peaked }}",
                availability_topic = AvailabilityTopic,
                device
            },
            JsonOpts);
        await PublishAsync(
            $"{options.DiscoveryPrefix}/binary_sensor/{options.DeviceId}/peaked/config",
            binaryConfig,
            retain: true,
            ct);
        var staleConfig = JsonSerializer.Serialize(
            new
            {
                name = "Data Stale",
                unique_id = $"{options.DeviceId}_data_stale",
                state_topic = StateTopic,
                value_template = "{{ value_json.data_stale }}",
                payload_on = "ON",
                payload_off = "OFF",
                device_class = "problem",
                availability_topic = AvailabilityTopic,
                device
            },
            JsonOpts);
        await PublishAsync(
            $"{options.DiscoveryPrefix}/binary_sensor/{options.DeviceId}/data_stale/config",
            staleConfig,
            retain: true,
            ct);
        var buttonConfig = JsonSerializer.Serialize(
            new
            {
                name = "Reset Session",
                unique_id = $"{options.DeviceId}_reset",
                command_topic = ResetCommandTopic,
                availability_topic = AvailabilityTopic,
                device
            },
            JsonOpts);
        await PublishAsync(
            $"{options.DiscoveryPrefix}/button/{options.DeviceId}/reset/config",
            buttonConfig,
            retain: true,
            ct);
        await PublishSensorConfigAsync(
            "session_summary",
            "Last Session Ended",
            null,
            "{{ value_json.session_end }}",
            device,
            ct,
            deviceClass: "timestamp",
            entityCategory: "diagnostic",
            stateTopicOverride: SessionSummaryTopic,
            jsonAttributesTopic: SessionSummaryTopic);
        if (options.DebugMode)
        {
            await PublishDebugDiscoveryAsync(device, ct);
        }
    }

    private async Task PublishDebugDiscoveryAsync(object device, CancellationToken ct)
    {
        var cameraConfig = JsonSerializer.Serialize(
            new
            {
                name = "Debug Image",
                unique_id = $"{options.DeviceId}_debug_image",
                topic = DebugImageTopic,
                availability_topic = AvailabilityTopic,
                device
            },
            JsonOpts);
        await PublishAsync(
            $"{options.DiscoveryPrefix}/camera/{options.DeviceId}/debug_image/config",
            cameraConfig,
            retain: true,
            ct);
        await PublishSensorConfigAsync(
            "detection_method",
            "Detection Method",
            null,
            "{{ value_json.method }}",
            device,
            ct,
            entityCategory: "diagnostic");
        await PublishSensorConfigAsync(
            "band_contrast",
            "Band Contrast",
            null,
            "{{ value_json.band_contrast }}",
            device,
            ct,
            entityCategory: "diagnostic");
        await PublishSensorConfigAsync(
            "dough_top_px",
            "Dough Top",
            "px",
            "{{ value_json.dough_top_px }}",
            device,
            ct,
            entityCategory: "diagnostic");
        await PublishSensorConfigAsync(
            "jar_bottom_px",
            "Jar Bottom",
            "px",
            "{{ value_json.jar_bottom_px }}",
            device,
            ct,
            entityCategory: "diagnostic");
        await PublishSensorConfigAsync(
            "jar_height_px",
            "Jar Height",
            "px",
            "{{ value_json.jar_height_px }}",
            device,
            ct,
            entityCategory: "diagnostic");
        await PublishSensorConfigAsync(
            "dough_height_px",
            "Dough Height",
            "px",
            "{{ value_json.dough_height_px }}",
            device,
            ct,
            entityCategory: "diagnostic");
    }

    private async Task PublishSensorConfigAsync(
        string key,
        string name,
        string? unit,
        string template,
        object device,
        CancellationToken ct,
        string? deviceClass = null,
        string? entityCategory = null,
        string? stateTopicOverride = null,
        string? jsonAttributesTopic = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["unique_id"] = $"{options.DeviceId}_{key}",
            ["state_topic"] = stateTopicOverride ?? DiagnosticsTopic,
            ["value_template"] = template,
            ["availability_topic"] = AvailabilityTopic,
            ["device"] = device
        };
        if (unit is not null) config["unit_of_measurement"] = unit;
        if (deviceClass is not null) config["device_class"] = deviceClass;
        if (entityCategory is not null) config["entity_category"] = entityCategory;
        if (jsonAttributesTopic is not null) config["json_attributes_topic"] = jsonAttributesTopic;
        await PublishAsync(
            $"{options.DiscoveryPrefix}/sensor/{options.DeviceId}/{key}/config",
            JsonSerializer.Serialize(config, JsonOpts),
            retain: true,
            ct);
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        _client.PublishAsync(
            new MqttApplicationMessageBuilder().WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .Build(),
            ct);

    private Task PublishRawAsync(string topic, byte[] payload, bool retain, CancellationToken ct) =>
        _client.PublishAsync(
            new MqttApplicationMessageBuilder().WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .Build(),
            ct);

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            await PublishAsync(AvailabilityTopic, "offline", retain: true, CancellationToken.None);
        _client.Dispose();
    }
}
