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
        You are a fast action-response assistant. user's message will consist of two parts: user-instruction and selected-text.
        You job is:
        1. Infer the user's intent from user-instruction and apply it to the provided text. selected-text may be empty.
        2. You should complete task in at most 3 turn.
        3. Quick response take higher priority.
        4. You can leverage provided tools to write back the modified text if needed.       
        5. After calling the tool, reply with exactly one concise sentence summarizing what was done.
        6. If No tools are needed, directly return the modified text with a concise explanation of what you did.

        """;

    private const string UserPromptTemplate = """
        Here is the selected text:
        {0}

        User instruction:
        {1}
        """;

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
        _logger.LogDebug("[LlmCommand] Sending command to LLM. SystemPrompt={SystemPrompt}, UserPrompt={UserPrompt}, Tools={Tools}",
            systemPrompt, userPrompt, toolNames);
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
