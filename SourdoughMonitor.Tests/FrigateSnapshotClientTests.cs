using System.Net;
using System.Net.Http.Headers;
using SourdoughMonitor.Config;
using SourdoughMonitor.Services;

namespace SourdoughMonitor.Tests;

public class FrigateSnapshotClientTests
{
    [Fact]
    public async Task GetLatestSnapshotAsync_UsesConfiguredSnapshotUrlAndBearerToken()
    {
        HttpRequestMessage? sentRequest = null;
        var handler = new StubHttpMessageHandler((request, cancellationToken) =>
        {
            sentRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        });

        var http = new HttpClient(handler);
        var options = new FrigateOptions
        {
            BaseUrl = "https://frigate.example",
            Camera = "battery_cam",
            SnapshotUrl = "https://frigate.example/api/battery_cam/latest.jpg?quality=90",
            AccessToken = "abc123"
        };

        var client = new FrigateSnapshotClient(http, options);
        var bytes = await client.GetLatestSnapshotAsync(CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.Equal([1, 2, 3], bytes);
        Assert.NotNull(sentRequest);
        Assert.Equal("https://frigate.example/api/battery_cam/latest.jpg?quality=90", sentRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", sentRequest.Headers.Authorization?.Scheme);
        Assert.Equal("abc123", sentRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetLatestSnapshotAsync_DoesNotThrowWhenSnapshotPersistenceFails()
    {
        var tempPath = Path.Combine(AppContext.BaseDirectory, "latest_snapshot.tmp");
        File.WriteAllBytes(tempPath, [1]);

        try
        {
            var handler = new StubHttpMessageHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([7, 8, 9])
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(response);
            });

            var client = new FrigateSnapshotClient(new HttpClient(handler), new FrigateOptions
            {
                BaseUrl = "https://frigate.example",
                Camera = "battery_cam"
            });
            var bytes = await client.GetLatestSnapshotAsync(CancellationToken.None);

            Assert.Equal([7, 8, 9], bytes);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath);
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
