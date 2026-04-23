namespace WhisperDesk.Transcript.Models;

public record TranscriptionHistoryEntry
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public TimeSpan Duration { get; init; }
    public string Language { get; init; } = "";
    public string RawText { get; init; } = "";
    public string ProcessedText { get; init; } = "";
    public string Source { get; init; } = "microphone";
    public string SttProvider { get; init; } = "";
    public string LlmProvider { get; init; } = "";
    public string ForegroundProcess { get; init; } = "";
    public string ForegroundWindowTitle { get; init; } = "";
}
