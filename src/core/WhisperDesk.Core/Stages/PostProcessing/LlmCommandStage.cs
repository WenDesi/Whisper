using Fluid;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Contract;
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
    private static readonly IReadOnlySet<SessionMode> AppliesToSet =
        new HashSet<SessionMode> { SessionMode.Instruct };

    private static readonly IFluidTemplate SystemPromptTemplate =
        PromptTemplateLoader.Load(typeof(LlmCommandStage).Assembly, "Command.liquid");

    private readonly ILogger<LlmCommandStage> _logger;
    private readonly ILlmProvider _llmProvider;

    public string Name => "LLM Command";
    public int Order => 200;
    public IReadOnlySet<SessionMode> AppliesTo => AppliesToSet;

    private const string UserPromptTemplate = """
        Here is the selected text:
        {0}

        User instruction:
        {1}
        """;

    private const string DraftUserPromptTemplate = """
        Here is the draft text:
        <draft-text>{0}</draft-text>

        User instruction:
        <user-instruction>{1}</user-instruction>
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
        if (string.IsNullOrWhiteSpace(toolContext.MainWindowTitle))
        {
            _logger.LogWarning("[LlmCommand] No valid window title in context. Skipping LLM command.");
            return text;
        }

        var systemPrompt = await SystemPromptTemplate.RenderAsync(new TemplateContext());

        var isDraftMode = !string.IsNullOrWhiteSpace(toolContext.DraftText);
        var userPrompt = isDraftMode
            ? string.Format(DraftUserPromptTemplate, toolContext.DraftText, text)
            : BuildNormalUserPrompt(toolContext.SelectedText, text);

        var toolNames = string.Join(", ", toolContext.Tools.Select(t => t.Name));
        _logger.LogDebug("[LlmCommand] Sending command to LLM. SystemPrompt={SystemPrompt}, UserPrompt={UserPrompt}, Tools={Tools}",
            systemPrompt, userPrompt, toolNames);

        string result;
        try
        {
            result = await _llmProvider.ProcessCommandAsync(
                systemPrompt,
                userPrompt,
                toolContext,
                new LlmRequestOptions { Temperature = 0.5f },
                ct);
        }
        catch (Exception ex) when (isDraftMode)
        {
            _logger.LogWarning(ex, "[LlmCommand] Draft correction failed; keeping original draft.");
            return toolContext.DraftText;
        }

        if (isDraftMode && string.IsNullOrWhiteSpace(result))
        {
            _logger.LogWarning("[LlmCommand] Draft correction returned empty text; keeping original draft.");
            return toolContext.DraftText;
        }

        _logger.LogInformation("[LlmCommand] Done: {InLen} -> {OutLen} chars.", text.Length, result.Length);
        return result;
    }

    private static string BuildNormalUserPrompt(string selected, string instruction)
    {
        var selectedInner = string.IsNullOrWhiteSpace(selected)
            ? "No text was selected. Apply the command to the entire transcript."
            : selected;
        var selectedText = $"<selected-text>{selectedInner}</selected-text>";
        return string.Format(UserPromptTemplate, selectedText, $"<user-instruction>{instruction}</user-instruction>");
    }
}
