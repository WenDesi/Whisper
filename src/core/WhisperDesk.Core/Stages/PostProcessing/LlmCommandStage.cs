using Fluid;
using System.Globalization;
using System.Security;
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
    private static readonly IFluidTemplate CommandInputTemplate =
        PromptTemplateLoader.Load(typeof(LlmCommandStage).Assembly, "CommandInput.liquid");
    private static readonly IFluidTemplate CorrectionPromptTemplate =
        PromptTemplateLoader.Load(typeof(LlmCommandStage).Assembly, "DraftCorrection.liquid");
    private static readonly IFluidTemplate CorrectionInputTemplate =
        PromptTemplateLoader.Load(typeof(LlmCommandStage).Assembly, "DraftCorrectionInput.liquid");

    private readonly ILogger<LlmCommandStage> _logger;
    private readonly ILlmProvider _llmProvider;

    public string Name => "LLM Command";
    public int Order => 200;
    public IReadOnlySet<SessionMode> AppliesTo => AppliesToSet;

    public LlmCommandStage(ILogger<LlmCommandStage> logger, ILlmProvider llmProvider)
    {
        _logger = logger;
        _llmProvider = llmProvider;
    }

    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct = default)
    {
        var instruction = context.RawTranscript;
        if (string.IsNullOrWhiteSpace(instruction))
        {
            _logger.LogInformation("[LlmCommand] Empty transcript; skipping LLM command.");
            return text;
        }

        var toolContext = context.ToolContext;
        var isDraftMode = !string.IsNullOrWhiteSpace(toolContext.DraftText);
        if (!isDraftMode && string.IsNullOrWhiteSpace(toolContext.MainWindowTitle))
        {
            _logger.LogWarning("[LlmCommand] No valid window title in context. Skipping LLM command.");
            return text;
        }

        var input = new TemplateContext();
        input.SetValue("instruction", Escape(instruction));
        string systemPrompt;
        string userPrompt;
        if (isDraftMode)
        {
            input.SetValue("original_text", Escape(string.IsNullOrEmpty(context.OriginalDraftText)
                ? toolContext.DraftText : context.OriginalDraftText));
            input.SetValue("current_draft", Escape(toolContext.DraftText));
            input.SetValue("turns", context.DraftCorrections.Select(turn => new Dictionary<string, string>
            {
                ["timestamp"] = turn.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                ["raw_instruction"] = Escape(turn.RawInstruction),
                ["output"] = Escape(turn.Output)
            }).ToArray());
            input.SetValue("current_round", context.DraftCorrections.Count + 1);
            input.SetValue("current_timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            systemPrompt = await CorrectionPromptTemplate.RenderAsync(new TemplateContext());
            userPrompt = await CorrectionInputTemplate.RenderAsync(input);
        }
        else
        {
            input.SetValue("selected_text", Escape(toolContext.SelectedText));
            systemPrompt = await SystemPromptTemplate.RenderAsync(new TemplateContext());
            userPrompt = await CommandInputTemplate.RenderAsync(input);
        }

        var toolNames = string.Join(", ", toolContext.Tools.Select(t => t.Name));
        _logger.LogDebug("[LlmCommand] Sending command to LLM. SystemPrompt={SystemPrompt}, UserPrompt={UserPrompt}, Tools={Tools}",
            systemPrompt, userPrompt, toolNames);

        var result = isDraftMode
            ? await _llmProvider.ProcessTextAsync(systemPrompt, userPrompt,
                new LlmRequestOptions { Temperature = 0 }, ct)
            : await _llmProvider.ProcessCommandAsync(
                systemPrompt,
                userPrompt,
                toolContext,
                new LlmRequestOptions { Temperature = 0.5f },
                ct);

        if (isDraftMode && string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("纠正模型未返回文字，原草稿未修改。");
        }

        _logger.LogInformation("[LlmCommand] Done: {InLen} -> {OutLen} chars.", text.Length, result.Length);
        return result;
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;
}
