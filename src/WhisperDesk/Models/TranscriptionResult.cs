namespace WhisperDesk.Models;

public record TranscriptionResult
{
    public required string RawText { get; init; }
    public required string CleanedText { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public TimeSpan AudioDuration { get; init; }
    public string? SourceFile { get; init; }
    public string Language { get; init; } = "zh";
}
