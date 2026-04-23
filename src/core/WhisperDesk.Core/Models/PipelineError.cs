namespace WhisperDesk.Core.Models;

/// <summary>
/// Describes a pipeline error with stage information.
/// </summary>
public record PipelineError
{
    /// <summary>Which stage/component failed.</summary>
    public required string Stage { get; init; }

    /// <summary>Human-readable error message.</summary>
    public required string Message { get; init; }

    /// <summary>The underlying exception, if any.</summary>
    public Exception? Exception { get; init; }
}
