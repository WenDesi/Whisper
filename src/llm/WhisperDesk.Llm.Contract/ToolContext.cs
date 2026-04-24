namespace WhisperDesk.Llm.Contract;

/// <summary>
/// LLM-agnostic tool context. Holds all registered tools and the executor
/// responsible for dispatching tool calls to real implementations.
/// This type has no dependency on any LLM provider SDK.
/// </summary>
public class ToolContext
{
    /// <summary>
    /// The tools exposed to the LLM. Each entry describes the schema; the LLM
    /// decides which to call and with what arguments.
    /// </summary>
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }

    /// <summary>
    /// Executes a tool call.
    /// Parameters:
    ///   toolName   – the <see cref="ToolDefinition.Name"/> the LLM chose
    ///   arguments  – raw JSON string produced by the LLM for that call
    ///   ct         – cancellation token
    /// Returns the tool result as a plain string that will be fed back to the LLM.
    /// </summary>
    public required Func<string, string, CancellationToken, Task<string>> ToolExecutor { get; init; }

    public required string SelectedText { get; init;}
    public required string MainWindowTitle { get; init;}
}
