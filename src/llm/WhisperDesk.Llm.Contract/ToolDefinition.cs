namespace WhisperDesk.Llm.Contract;

/// <summary>
/// LLM-agnostic description of a callable tool.
/// <see cref="ParametersSchema"/> must be a valid JSON Schema object string,
/// e.g. <c>{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}</c>.
/// </summary>
public class ToolDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>JSON Schema string describing the tool's parameters.</summary>
    public required string ParametersSchema { get; init; }
}
