using System.Net.Http.Headers;
using System.Text.Json;
using SourdoughMonitor;
using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Mqtt;
using SourdoughMonitor.Services;
using SourdoughMonitor.Vision;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var options = builder.Configuration.GetSection("Monitor").Get<MonitorOptions>()
    ?? throw new InvalidOperationException("Missing 'Monitor' configuration section");

var supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
if (supervisorToken is not null)
{
    using var supervisorHttp = new HttpClient { BaseAddress = new Uri("http://supervisor") };
    supervisorHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supervisorToken);

    var mqttInfo = await FetchMqttServiceInfoAsync(supervisorHttp);
    options.Mqtt.Host = mqttInfo.Host;
    options.Mqtt.Port = mqttInfo.Port;
    options.Mqtt.Username = mqttInfo.Username;
    options.Mqtt.Password = mqttInfo.Password;
}

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
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();

static async Task<MqttServiceInfo> FetchMqttServiceInfoAsync(HttpClient http)
{
    using var response = await http.GetAsync("/services/mqtt");
    response.EnsureSuccessStatusCode();

    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    var data = json.RootElement.GetProperty("data");

    return new MqttServiceInfo(
        data.GetProperty("host").GetString()!,
        data.GetProperty("port").GetInt32(),
        data.TryGetProperty("username", out var u) ? u.GetString() : null,
        data.TryGetProperty("password", out var p) ? p.GetString() : null);
}

internal sealed record MqttServiceInfo(string Host, int Port, string? Username, string? Password);