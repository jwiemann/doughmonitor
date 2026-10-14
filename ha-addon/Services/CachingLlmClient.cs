using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourdoughMonitor.Services;

/// <summary>On-disk response cache for offline replays and model benchmarks: a repeated
/// (model, prompt, image) triple is answered from disk, so reruns cost nothing and are
/// deterministic. Only successful completions are cached.</summary>
public sealed class CachingLlmClient(IVisionLlmClient inner, string directory) : IVisionLlmClient
{
    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Key(request) + ".json");
        if (File.Exists(path))
        {
            var cached = JsonSerializer.Deserialize<LlmCompletion>(await File.ReadAllTextAsync(path, ct));
            if (cached is not null) return cached with { FromCache = true };
        }
        var fresh = await inner.CompleteAsync(request, ct);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(fresh), ct);
        File.Move(temp, path, overwrite: true);
        return fresh;
    }

    private static string Key(LlmRequest request)
    {
        using var sha = SHA256.Create();
        void Add(byte[] data) => sha.TransformBlock(data, 0, data.Length, null, 0);
        Add(Encoding.UTF8.GetBytes(request.Model + "\n" + request.SystemPrompt + "\n" + request.UserText + "\n"
                                   + (request.Sample > 0 ? $"sample{request.Sample}@{request.Temperature}\n" : "")));
        Add(request.Jpeg);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
