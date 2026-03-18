namespace WhisperDesk.Core.Models;

/// <summary>
/// Result of a completed pipeline session.
/// </summary>
public record PipelineResult
{
    /// <summary>Raw transcript from STT (before post-processing).</summary>
    public required string RawTranscript { get; init; }

    /// <summary>Final processed text (after all post-processing stages).</summary>
    public required string ProcessedText { get; init; }

    /// <summary>Duration of captured audio.</summary>
    public TimeSpan AudioDuration { get; init; }

    /// <summary>When the session completed.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>Detected or configured language.</summary>
    public string Language { get; init; } = "zh";

    /// <summary>Source file path if processing a file, null for microphone.</summary>
    public string? SourceFile { get; init; }
}
