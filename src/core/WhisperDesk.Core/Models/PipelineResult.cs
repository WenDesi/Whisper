namespace WhisperDesk.Core.Models;

public record PipelineResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    public required string RawTranscript { get; init; }

    public required string ProcessedText { get; init; }

    public TimeSpan AudioDuration { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.Now;

    public string Language { get; init; } = "zh";

    public string? SourceFile { get; init; }

    public string SttProvider { get; init; } = "";

    public string LlmProvider { get; init; } = "";
}
