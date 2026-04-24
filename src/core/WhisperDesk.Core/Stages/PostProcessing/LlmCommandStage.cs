using System.ComponentModel.Design.Serialization;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Llm.Contract;

namespace WhisperDesk.Core.Stages.PostProcessing;

/// <summary>
/// Post-processing stage that applies a user-supplied LLM instruction to the transcribed text.
/// The command is injected at construction time, making this stage reusable for any
/// prompt-driven transformation (summarise, translate, extract action items, etc.).
/// </summary>
public class LlmCommandStage : IPostProcessingStage
{
    private readonly ILogger<LlmCommandStage> _logger;
    private readonly ILlmProvider _llmProvider;

    public string Name => "LLM Command";
    public int Order => 200; // Run after cleanup stages

    private const string SystemPromptTemplate = """
        You are a fast text-editing assistant. Infer the user's intent from their message and apply it to the provided text.
        You MUST call the `apply_result` tool to write back the modified text — never output it as plain text.
        After calling the tool, reply with exactly one concise sentence summarizing what was done.

        """;

    private const string UserPromptTemplate = """
        Here is the selected text:
        {0}

        User instruction:
        {1}

        Remember, you MUST call the `{2}` tools to write back the modified text — never output it as plain text.
        """;

    private static readonly ToolDefinition ApplyResultTool = new()
    {
        Name = "apply_result",
        Description = "Write the modified text back to the user's context.",
        ParametersSchema = """{"type":"object","properties":{"text":{"type":"string","description":"The fully modified text to apply."}},"required":["text"]}"""
    };

    public LlmCommandStage(ILogger<LlmCommandStage> logger, ILlmProvider llmProvider)
    {
        _logger = logger;
        _llmProvider = llmProvider;
    }

    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct = default)
    {
        var toolContext = context.ToolContext;
        if(string.IsNullOrWhiteSpace(toolContext.MainWindowTitle))
        {
            _logger.LogWarning("[LlmCommand] No valid window title in context. Skipping LLM command.");
            return text;
        }
        
        var systemPrompt = string.Format(SystemPromptTemplate);
        var selectedText = $"<selected-text>{(string.IsNullOrWhiteSpace(toolContext.SelectedText) ?
            "No text was selected. Apply the command to the entire transcript." :
            $"{toolContext.SelectedText}</selected-text>")}";
        var toolNames = String.Join(", ", toolContext.Tools.Select(t => t.Name));
        var userPrompt = string.Format(UserPromptTemplate, selectedText, $"<user-instruction>${text}</user-instruction>", toolNames);
        var result = await _llmProvider.ProcessCommandAsync(
            systemPrompt,
            userPrompt,
            toolContext,
            new LlmRequestOptions { Temperature = 0.5f },
            ct);
        _logger.LogInformation("[LlmCommand] Done: {InLen} -> {OutLen} chars.", text.Length, result.Length);
        return result;
    }
}
