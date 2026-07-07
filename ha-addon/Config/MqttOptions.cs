namespace SourdoughMonitor.Config;

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string DeviceId { get; init; } = "sourdough_monitor";
    public string DiscoveryPrefix { get; init; } = "homeassistant";
    public bool DebugMode { get; init; }
}