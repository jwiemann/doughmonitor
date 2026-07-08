using SourdoughMonitor.Config;

namespace SourdoughMonitor.Config;

public sealed class MonitorOptions
{
    public required FrigateOptions Frigate { get; init; }

    public required MqttOptions Mqtt { get; init; }

    public AnalysisOptions Analysis { get; init; } = new();

    public VisionOptions Vision { get; init; } = new();
}
