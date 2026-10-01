using System.Net.Http.Headers;

using SourdoughMonitor.Config;

namespace SourdoughMonitor.Services;

public sealed class FrigateSnapshotClient(HttpClient http, FrigateOptions options)
{
    private static readonly string? SupervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");

    public async Task<byte[]?> GetLatestSnapshotAsync(CancellationToken ct)
    {
        var url = ResolveSnapshotUrl();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (SupervisorToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SupervisorToken);
        }
        else if (!string.IsNullOrWhiteSpace(options.AccessToken))
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
        }
        catch (UnauthorizedAccessException) when (ct.IsCancellationRequested is false)
        {
        }
        ArchiveSnapshot(bytes);
        return bytes;
    }

    /// <summary>Timestamped raw-frame archive for offline replay analysis. Best-effort:
    /// storage problems must never break the sampling loop.</summary>
    private void ArchiveSnapshot(byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(options.SnapshotArchiveDirectory)) return;
        try
        {
            var directory = Path.IsPathFullyQualified(options.SnapshotArchiveDirectory)
                ? options.SnapshotArchiveDirectory
                : Path.Combine(AppContext.BaseDirectory, options.SnapshotArchiveDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.Now:yyyyMMdd_HHmmssfff}.jpg");
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string ResolveSnapshotUrl()
    {
        if (!string.IsNullOrWhiteSpace(options.SnapshotUrl))
        {
            return options.SnapshotUrl;
        }
        if (SupervisorToken is not null)
        {
            return $"http://supervisor/core/api/camera_proxy/camera.{options.Camera}";
        }
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            var baseUrl = options.BaseUrl.TrimEnd('/');
            return $"{baseUrl}/api/{options.Camera}/latest.jpg?quality={options.SnapshotQuality}&height={options.SnapshotHeight}";
        }
        throw new InvalidOperationException(
            "No snapshot URL configured. Set SUPERVISOR_TOKEN for addon mode, "
            + "or configure Frigate:BaseUrl (or Frigate:SnapshotUrl) in appsettings for local dev.");
    }
}