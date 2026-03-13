namespace WhisperDesk.Services;

/// <summary>
/// Text cleanup provider interface. Uses LLM to remove fillers and fix grammar.
/// </summary>
public interface ITextCleanupService
{
    /// <summary>
    /// Clean up raw transcription text: remove fillers, fix grammar/punctuation.
    /// </summary>
    Task<string> CleanupTextAsync(string rawText, CancellationToken ct = default);
}
