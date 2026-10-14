namespace SourdoughMonitor.Config;

/// <summary>Vision-LLM detector settings (OpenRouter chat-completions API).</summary>
public sealed class LlmOptions
{
    /// <summary>OpenRouter API key. Never logged.</summary>
    public string? ApiKey { get; init; }

    /// <summary>OpenRouter model slug; must accept image input.</summary>
    public string Model { get; init; } = "google/gemini-3.8-flash";

    /// <summary>Independent draws of every frame per cycle; the detector uses the median level.
    /// 1 disables sampling. Cost scales linearly.</summary>
    public int SamplesPerCycle { get; init; } = 3;

    /// <summary>Temperature of draws after the first (the first is always deterministic at 0), so
    /// the draws can differ and their spread measures the model's own uncertainty.</summary>
    public double SampleTemperature { get; init; } = 0.4;

    /// <summary>A cycle whose samples differ by more than this many pixels counts as noisy in the
    /// running spread statistics.</summary>
    public double SpreadWarnPx { get; init; } = 10;

    /// <summary>Completion budget per call. Reasoning models count thinking tokens against it, so
    /// a small value truncates the JSON answer.</summary>
    public int MaxTokens { get; init; } = 2000;

    /// <summary>OpenRouter reasoning effort ("minimal", "low", ...); empty omits the field.
    /// Thinking adds cost and latency and gains nothing for point localisation.</summary>
    public string ReasoningEffort { get; init; } = "minimal";

    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    /// <summary>The cropped frame is downscaled so its longer edge is at most this many pixels
    /// before upload; fewer pixels means fewer input tokens.</summary>
    public int ImageMaxEdge { get; init; } = 640;

    public int JpegQuality { get; init; } = 80;

    /// <summary>Hard cap on API calls per UTC day; further frames report "llm_budget" until
    /// the next day. Protects against runaway cost.</summary>
    public int MaxCallsPerDay { get; init; } = 4500;

    /// <summary>JSON file holding billed calls and cost per day and in total, so the totals survive
    /// restarts. In the add-on this is under /data. Null keeps them in memory only.</summary>
    public string? UsageFilePath { get; init; }

    public double RequestTimeoutSeconds { get; init; } = 60;

    /// <summary>Retries after the first attempt for 429, 5xx and timeouts.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Responses reporting a lower self-confidence are rejected.</summary>
    public double MinConfidence { get; init; } = 0.3;

    /// <summary>Accepted jar outlines kept for the median jar geometry (top, bottom, sides).
    /// The jar does not move, so the median stabilises bottom and top across noisy answers.</summary>
    public int GeometryWindow { get; init; } = 15;

    /// <summary>Fraction of the frame size by which the median of the newest outlines may differ
    /// from the older median before it counts as a camera move.</summary>
    public double GeometryJumpFraction { get; init; } = 0.08;

    /// <summary>Number of newest outlines compared with the older median (must be at most half of
    /// <see cref="GeometryWindow"/>); a camera move is confirmed when their median shifted.</summary>
    public int SceneChangeFrames { get; init; } = 5;
}
