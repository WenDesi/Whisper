using WhisperDesk.Llm.Contract;

namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// Context passed to each post-processing stage, providing access to
/// the original raw transcript and session metadata.
/// </summary>
public class PostProcessingContext
{
    /// <summary>The raw transcript as returned by the STT provider (never modified).</summary>
    public required string RawTranscript { get; init; }

    /// <summary>The language detected or configured for the session.</summary>
    public string? Language { get; init; }

    /// <summary>Phrase hints that were used for this session.</summary>
    public IReadOnlyList<string> PhraseHints { get; init; } = [];

    /// <summary>Tool context for LLM-driven post-processing stages.</summary>
    public required ToolContext ToolContext { get; init; }
}
