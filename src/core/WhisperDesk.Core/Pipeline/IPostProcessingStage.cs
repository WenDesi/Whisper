namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// A text post-processing stage executed after STT completes.
/// Stages run sequentially in Order. Each receives the output of the previous stage.
/// </summary>
public interface IPostProcessingStage
{
    /// <summary>Display name for logging/diagnostics.</summary>
    string Name { get; }

    /// <summary>Execution order (lower = earlier).</summary>
    int Order { get; }

    /// <summary>
    /// Process text from the previous stage (or raw STT output for the first stage).
    /// Return the processed text for the next stage.
    /// </summary>
    Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct = default);
}
