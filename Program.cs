using SourdoughMonitor;
using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Mqtt;
using SourdoughMonitor.Services;
using SourdoughMonitor.Vision;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Critical);

var options = builder.Configuration.GetSection("Monitor").Get<MonitorOptions>()
    ?? throw new InvalidOperationException("Missing 'Monitor' configuration section");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Frigate);
builder.Services.AddSingleton(options.Mqtt);
builder.Services.AddSingleton(options.Analysis);
builder.Services.AddSingleton(options.Vision);

builder.Services.AddHttpClient<FrigateSnapshotClient>();
builder.Services.AddSingleton<JarLevelDetector>();
builder.Services.AddSingleton<RiseAnalyzer>();
builder.Services.AddSingleton(new GrowthOptions());
builder.Services.AddSingleton<GrowthTracker>();
builder.Services.AddSingleton<HaMqttPublisher>();
builder.Services.AddSingleton<AsciiConsoleRenderer>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
