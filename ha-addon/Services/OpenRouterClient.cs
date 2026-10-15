using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using SourdoughMonitor.Config;

namespace SourdoughMonitor.Services;

/// <summary>OpenRouter chat-completions client for a single image + prompt, returning the
/// assistant's JSON text plus token usage and the billed cost.</summary>
public sealed class OpenRouterClient(HttpClient http, LlmOptions options) : IVisionLlmClient
{
    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new LlmException("OpenRouter API key is not configured (Monitor:Llm:ApiKey).");
        var body = BuildBody(request);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(body, ct);
            }
            catch (LlmException ex) when (attempt < options.MaxRetries && IsTransient(ex.StatusCode))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
    }

    private static bool IsTransient(int? status) =>
        status is null or 408 or 429 or >= 500;

    private async Task<LlmCompletion> SendOnceAsync(string body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, options.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Headers.Add("X-Title", "Sourdough Monitor");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        HttpResponseMessage response;
        string text;
        try
        {
            response = await http.SendAsync(message, timeout.Token);
            text = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"OpenRouter request failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("OpenRouter request timed out.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode == HttpStatusCode.PaymentRequired
                    ? " (insufficient credits or max_tokens too high)"
                    : "";
                throw new LlmException(
                    $"OpenRouter HTTP {(int)response.StatusCode}{hint}: {Truncate(text)}",
                    (int)response.StatusCode);
            }
        }
        return ParseResponse(text);
    }

    internal static LlmCompletion ParseResponse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new LlmException($"OpenRouter returned non-JSON: {Truncate(json)}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : (int?)null;
                throw new LlmException($"OpenRouter error: {Truncate(error.ToString())}", code);
            }
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var msg)
                || !msg.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                throw new LlmException($"OpenRouter response has no message content: {Truncate(json)}");
            }
            int prompt = 0, completion = 0;
            double? cost = null;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi)) prompt = pi;
                if (usage.TryGetProperty("completion_tokens", out var q) && q.TryGetInt32(out var qi)) completion = qi;
                if (usage.TryGetProperty("cost", out var k) && k.ValueKind == JsonValueKind.Number) cost = k.GetDouble();
            }
            return new LlmCompletion(content.GetString()!, prompt, completion, cost);
        }
    }

    private string BuildBody(LlmRequest request)
    {
        var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(request.Jpeg);
        var payload = new
        {
            model = request.Model,
            temperature = request.Temperature,
            max_tokens = options.MaxTokens,
            usage = new { include = true },
            reasoning = string.IsNullOrWhiteSpace(options.ReasoningEffort)
                ? null
                : options.ReasoningEffort.Equals("off", StringComparison.OrdinalIgnoreCase)
                    ? new { effort = (string?)null, enabled = (bool?)false }
                    : new { effort = options.ReasoningEffort, enabled = (bool?)null },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "jar_annotation", strict = true, schema = request.ResponseSchema }
            },
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = request.UserText },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
