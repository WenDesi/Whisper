using Fluid;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Contract;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Llm.Contract;

namespace WhisperDesk.Core.Stages.PostProcessing;

/// <summary>
/// Post-processing stage that uses an LLM to clean up transcribed text:
/// remove fillers, fix grammar/punctuation, preserve technical terms.
/// </summary>
public class LlmTextCleanupStage : IPostProcessingStage
{
    private static readonly IReadOnlySet<SessionMode> AppliesToSet =
        new HashSet<SessionMode> { SessionMode.Transcribe, SessionMode.Instruct };

    private static readonly IFluidTemplate SystemPromptTemplate =
        PromptTemplateLoader.Load(typeof(LlmTextCleanupStage).Assembly, "Cleanup.liquid");

    private readonly ILogger<LlmTextCleanupStage> _logger;
    private readonly ILlmProvider _llmProvider;

    public string Name => "LLM Text Cleanup";
    public int Order => 100;
    public IReadOnlySet<SessionMode> AppliesTo => AppliesToSet;

    public LlmTextCleanupStage(ILogger<LlmTextCleanupStage> logger, ILlmProvider llmProvider)
    {
        _logger = logger;
        _llmProvider = llmProvider;
    }

    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct = default)
    {
        _logger.LogInformation("[LlmCleanup] Cleaning {Length} chars via {Provider}.", text.Length, _llmProvider.Name);

        var systemPrompt = await SystemPromptTemplate.RenderAsync(new TemplateContext());

        var result = await _llmProvider.ProcessTextAsync(
            systemPrompt,
            text,
            new LlmRequestOptions { Temperature = 0.3f },
            ct);

        _logger.LogInformation("[LlmCleanup] Cleanup done: {InLen} -> {OutLen} chars.", text.Length, result.Length);
        return result;
    }
}
