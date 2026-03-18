namespace WhisperDesk.Core.Providers.Llm;

/// <summary>
/// Options for LLM requests. Providers use what they support.
/// </summary>
public class LlmRequestOptions
{
    /// <summary>Temperature (0.0 = deterministic, 1.0 = creative). Default: 0.3 for cleanup tasks.</summary>
    public float Temperature { get; init; } = 0.3f;

    /// <summary>Maximum tokens in the response.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Provider-specific options.</summary>
    public IReadOnlyDictionary<string, object> ProviderOptions { get; init; } = new Dictionary<string, object>();
}
