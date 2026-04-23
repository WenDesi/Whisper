namespace WhisperDesk.Core.Contract;

public record PipelineError
{
    public required string Stage { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}
