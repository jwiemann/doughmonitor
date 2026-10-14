namespace SourdoughMonitor.Services;

/// <summary>One image-plus-text request to a vision LLM that must answer with JSON.
/// <paramref name="Sample"/> numbers repeated draws of the same frame (0 = first); it only
/// distinguishes cache entries, since a sampled draw at a temperature above 0 differs per call.</summary>
public sealed record LlmRequest(
    string Model,
    string SystemPrompt,
    string UserText,
    byte[] Jpeg,
    object ResponseSchema,
    double Temperature = 0,
    int Sample = 0);

public sealed record LlmCompletion(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    double? CostUsd,
    bool FromCache = false);

public sealed class LlmException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

public interface IVisionLlmClient
{
    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct);
}
