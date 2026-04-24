namespace WhisperDesk.Llm.Contract;

public interface ILlmProvider
{
    string Name { get; }

    Task<string> ProcessTextAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        CancellationToken ct = default);

    IAsyncEnumerable<string> ProcessTextStreamingAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Runs a simple agent loop: the LLM may call tools zero or more times before
    /// producing a final text response. Tool dispatching is handled by
    /// <paramref name="toolContext"/>, which is provider-agnostic.
    /// </summary>
    Task<string> ProcessCommandAsync(
        string systemPrompt,
        string userText,
        ToolContext toolContext,
        LlmRequestOptions? options = null,
        CancellationToken ct = default);
}
