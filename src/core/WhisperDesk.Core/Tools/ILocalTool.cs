namespace WhisperDesk.Core.Tools;

/// <summary>
/// A tool that executes locally inside the Core process, without round-tripping
/// through the UI command channel. Contrast with the remote commands dispatched
/// via <c>StreamingPipeline.LocalCommandExecuted</c> (append/replace/read_all_context),
/// which require the UI to act on the foreground window.
/// </summary>
public interface ILocalTool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema string describing the tool's parameters.</summary>
    string ParametersSchema { get; }

    /// <summary>
    /// Executes the tool. <paramref name="argumentsJson"/> is the raw JSON arguments
    /// produced by the LLM. Returned string is fed back to the LLM as the tool result.
    /// </summary>
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct);
}
