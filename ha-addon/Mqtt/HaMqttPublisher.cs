using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Mqtt;

/// <summary>Publishes readings and HA MQTT discovery config; listens for a reset command.</summary>
public sealed class HaMqttPublisher(MqttOptions options) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();

    private string StateTopic => $"{options.DeviceId}/state";
    private string AvailabilityTopic => $"{options.DeviceId}/availability";
    public string ResetCommandTopic => $"{options.DeviceId}/cmd/reset";

    public event Func<Task>? ResetRequested;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
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
        var payload = JsonSerializer.Serialize(new
        {
            rise_percent = reading.RisePercent,
            rise_rate = reading.RiseRatePercentPerHour,
            predicted_peak_percent = reading.PredictedPeakPercent,
            peak_eta = reading.PredictedPeakTime?.ToString("O"),
            peaked = reading.Peaked ? "ON" : "OFF",
            new_session = reading.NewSession,
            last_update = reading.Time.ToString("O")
        }, JsonOpts);

        await PublishAsync(StateTopic, payload, retain: true, ct);
    }

    public Task PublishUnavailableMeasurementAsync(CancellationToken ct) =>
        PublishAsync($"{options.DeviceId}/measurement_status", "no_reading", retain: false, ct);

    private async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        var device = new
        {
            identifiers = new[] { options.DeviceId },
            name = "Sourdough Monitor",
            manufacturer = "DIY",
            model = "JarWatch 1.0"
        };

        await PublishSensorConfigAsync("rise_percent", "Starter Rise", "%", "{{ value_json.rise_percent }}", device, ct);
        await PublishSensorConfigAsync("rise_rate", "Starter Rise Rate", "%/h", "{{ value_json.rise_rate }}", device, ct);
        await PublishSensorConfigAsync("predicted_peak_percent", "Predicted Peak Rise", "%", "{{ value_json.predicted_peak_percent }}", device, ct);
        await PublishSensorConfigAsync("peak_eta", "Peak ETA", null, "{{ value_json.peak_eta }}", device, ct, deviceClass: "timestamp");

        var binaryConfig = JsonSerializer.Serialize(new
        {
            name = "Starter Peaked",
            unique_id = $"{options.DeviceId}_peaked",
            state_topic = StateTopic,
            value_template = "{{ value_json.peaked }}",
            availability_topic = AvailabilityTopic,
            device
        }, JsonOpts);
        await PublishAsync($"{options.DiscoveryPrefix}/binary_sensor/{options.DeviceId}/peaked/config", binaryConfig, retain: true, ct);

        var buttonConfig = JsonSerializer.Serialize(new
        {
            name = "Reset Session",
            unique_id = $"{options.DeviceId}_reset",
            command_topic = ResetCommandTopic,
            availability_topic = AvailabilityTopic,
            device
        }, JsonOpts);
        await PublishAsync($"{options.DiscoveryPrefix}/button/{options.DeviceId}/reset/config", buttonConfig, retain: true, ct);
    }

    private async Task PublishSensorConfigAsync(
        string key, string name, string? unit, string template, object device,
        CancellationToken ct, string? deviceClass = null)
    {
        var config = JsonSerializer.Serialize(new
        {
            name,
            unique_id = $"{options.DeviceId}_{key}",
            state_topic = StateTopic,
            value_template = template,
            unit_of_measurement = unit,
            device_class = deviceClass,
            availability_topic = AvailabilityTopic,
            device
        }, JsonOpts);

        await PublishAsync($"{options.DiscoveryPrefix}/sensor/{options.DeviceId}/{key}/config", config, retain: true, ct);
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build(), ct);

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            await PublishAsync(AvailabilityTopic, "offline", retain: true, CancellationToken.None);
        _client.Dispose();
    }
}
