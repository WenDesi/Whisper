using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Core.Providers.Llm;

namespace WhisperDesk.Core.Stages.PostProcessing;

/// <summary>
/// Post-processing stage that uses an LLM to clean up transcribed text:
/// remove fillers, fix grammar/punctuation, preserve technical terms.
/// </summary>
public class LlmTextCleanupStage : IPostProcessingStage
{
    private readonly ILogger<LlmTextCleanupStage> _logger;
    private readonly ILlmProvider _llmProvider;

    public string Name => "LLM Text Cleanup";
    public int Order => 100; // Run after any earlier stages

    private const string SystemPrompt = """
        You are a transcription cleanup assistant. Your job is to:
        1. Remove filler words (umm, uh, 嗯, 啊, 那个, 就是, 然后 etc.)
        2. Fix grammar and punctuation
        3. Keep the original meaning and tone intact
        4. Preserve all technical terms exactly as spoken (e.g., Redis, Kubernetes, API, Docker)
        5. Keep the original language - if Chinese, output Chinese; if English, output English
        6. For mixed language, keep technical English terms within Chinese text
        7. Do NOT translate, summarize, or add content
        8. Output ONLY the cleaned text, nothing else
        """;

    public LlmTextCleanupStage(ILogger<LlmTextCleanupStage> logger, ILlmProvider llmProvider)
    {
        _logger = logger;
        _llmProvider = llmProvider;
    }

    [Trace]
    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct = default)
    {
        _logger.LogInformation("[LlmCleanup] Cleaning {Length} chars via {Provider}.", text.Length, _llmProvider.Name);

        var result = await _llmProvider.ProcessTextAsync(
            SystemPrompt,
            text,
            new LlmRequestOptions { Temperature = 0.3f },
            ct);

        _logger.LogInformation("[LlmCleanup] Cleanup done: {InLen} -> {OutLen} chars.", text.Length, result.Length);

        return result;
    }
}
