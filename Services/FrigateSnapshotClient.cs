using System.Net.Http.Headers;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Services;

public sealed class FrigateSnapshotClient(HttpClient http, FrigateOptions options)
{
    public async Task<byte[]?> GetLatestSnapshotAsync(CancellationToken ct)
    {
        var url = ResolveSnapshotUrl();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await http.SendAsync(request, timeoutCts.Token);
        if (!response.IsSuccessStatusCode) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "latest_snapshot.jpg");
            var tempPath = Path.Combine(AppContext.BaseDirectory, "latest_snapshot.tmp");

            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            if (File.Exists(path))
            {
                File.Copy(tempPath, path, overwrite: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (IOException) when (ct.IsCancellationRequested is false)
        {
            // Snapshot retrieval is still successful even if local persistence fails.
        }
        catch (UnauthorizedAccessException) when (ct.IsCancellationRequested is false)
        {
            // Snapshot retrieval is still successful even if local persistence fails.
        }

        return bytes;
    }

    private string ResolveSnapshotUrl()
    {
        if (!string.IsNullOrWhiteSpace(options.SnapshotUrl))
        {
            return options.SnapshotUrl;
        }

        var baseUrl = options.BaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/{options.Camera}/latest.jpg?quality=90&height=1080&width=1920";
    }
}
