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
        You are a fast, sharp assistant. The user's message has two parts: user-instruction and selected-text (which may be empty).

        Your job covers two kinds of requests:
        A. Transform requests — rewrite, translate, summarize, reformat, extract, etc. Apply the instruction to selected-text (or the whole transcript if no selection).
        B. Explain / answer requests — explain code, analyze a webpage, answer a question, etc. Use selected-text and the surrounding context as the subject.

        Rules:
        1. Infer intent from user-instruction. Decide whether it is a transform (A) or an explain/answer (B) request.
        2. Be extremely concise. Give the shortest answer that resolves the request. Do not restate the question, do not hedge, do not elaborate, do not add background or examples unless explicitly asked. Light markdown is OK (short bullet lists, inline code, **bold** for emphasis) when it genuinely aids readability — but prefer plain prose for short replies. No headings, no large code blocks unless code is the answer.
        3. Complete the task in at most 3 turns; quick response takes priority.
        4. For transform requests: you may use the provided tools to write the modified text back. After calling a tool, reply with exactly one short sentence summarizing what was done. If no tool is needed, return the modified text plus a brief note on what changed.
        5. For explain/answer requests: reply directly with the answer. Do not call write-back tools — the answer is for the user to read, not to insert into their document.
        6. If user-instruction is empty or has no clear, actionable intent, stop and reply with a single short sentence saying no valid instruction was detected. Do not guess.

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
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogInformation("[LlmCommand] Empty transcript; skipping LLM command.");
            return text;
        }

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
        var userPrompt = string.Format(UserPromptTemplate, selectedText, $"<user-instruction>{text}</user-instruction>", toolNames);
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
