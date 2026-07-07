namespace SourdoughMonitor.Config;

public sealed class MonitorOptions
{
    public required FrigateOptions Frigate { get; init; }
    public required MqttOptions Mqtt { get; init; }
    public AnalysisOptions Analysis { get; init; } = new();
    public VisionOptions Vision { get; init; } = new();
}

public sealed class FrigateOptions
{
    public required string Camera { get; init; }
    public string? SnapshotUrl { get; init; }
    public string? BaseUrl { get; init; }
    public string? AccessToken { get; init; }
    public int SampleIntervalMinutes { get; init; } = 10;
}

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

public sealed class AnalysisOptions
{
    public int SlopeWindowMinutes { get; init; } = 40;
    public double ResetDropFraction { get; init; } = 0.25;
    public int MinSamplesForFit { get; init; } = 8;
    public double MaxEtaRelativeStdError { get; init; } = 0.15;
    public int PeakConfirmWindows { get; init; } = 3;
    public int MaxSessionHours { get; init; } = 36;
    public string? StateFilePath { get; init; } = "sourdough_state.json";
}

public sealed class VisionOptions
{
    public int? RoiX { get; init; }
    public int? RoiY { get; init; }
    public int? RoiWidth { get; init; }
    public int? RoiHeight { get; init; }
    public double MinJarWallFraction { get; init; } = 0.08;
    public double MinJarWidthFraction { get; init; } = 0.04;
    public bool DebugSaveAnnotatedImages { get; init; } = true;
    public string DebugOutputDirectory { get; init; } = "debug";
}
