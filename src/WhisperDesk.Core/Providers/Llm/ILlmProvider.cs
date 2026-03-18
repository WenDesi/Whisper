namespace WhisperDesk.Core.Providers.Llm;

/// <summary>
/// Pluggable LLM provider for text processing (cleanup, rewriting, etc.).
/// </summary>
public interface ILlmProvider
{
    /// <summary>Provider display name.</summary>
    string Name { get; }

    /// <summary>Single-turn text processing (fire-and-wait).</summary>
    Task<string> ProcessTextAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming variant for real-time UI updates.
    /// Yields text chunks as they arrive from the LLM.
    /// </summary>
    IAsyncEnumerable<string> ProcessTextStreamingAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        CancellationToken ct = default);
}
