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

    /// <summary>Minutes without a usable measurement before the published reading is
    /// declared stale: the state topic then carries data_stale=ON and the measured values
    /// are cleared, so a retained peak ETA or rise value can never masquerade as current.</summary>
    public int StaleAfterMinutes { get; init; } = 10;
}