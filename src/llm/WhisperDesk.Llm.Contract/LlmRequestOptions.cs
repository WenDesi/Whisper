namespace WhisperDesk.Llm.Contract;

public class LlmRequestOptions
{
    public float Temperature { get; init; } = 0.3f;
    public int? MaxTokens { get; init; }
    public IReadOnlyDictionary<string, object> ProviderOptions { get; init; } = new Dictionary<string, object>();
}
