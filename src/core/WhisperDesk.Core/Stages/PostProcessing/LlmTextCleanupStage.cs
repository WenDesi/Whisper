using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
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
    /// <summary>Transcripts shorter than this skip the LLM entirely — not worth the latency.</summary>
    private const int MinCleanupLength = 10;

    private static readonly IReadOnlySet<SessionMode> AppliesToSet =
        new HashSet<SessionMode> { SessionMode.Transcribe, SessionMode.Instruct };

    private static readonly IFluidTemplate SystemPromptTemplate =
        PromptTemplateLoader.Load(typeof(LlmTextCleanupStage).Assembly, "Cleanup.liquid");
    private static readonly IFluidTemplate InputTemplate =
        PromptTemplateLoader.Load(typeof(LlmTextCleanupStage).Assembly, "CleanupInput.liquid");
    private static readonly JsonSerializerOptions TranscriptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

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
        if (text.Length < MinCleanupLength)
        {
            _logger.LogInformation("[LlmCleanup] Skipping cleanup for short text ({Length} < {Min} chars).",
                text.Length, MinCleanupLength);
            return text;
        }

        _logger.LogInformation("[LlmCleanup] Cleaning {Length} chars via {Provider}.", text.Length, _llmProvider.Name);

        var systemPrompt = await SystemPromptTemplate.RenderAsync(new TemplateContext());
        var inputContext = new TemplateContext();
        inputContext.SetValue("transcript_json", JsonSerializer.Serialize(text, TranscriptJsonOptions));
        var input = await InputTemplate.RenderAsync(inputContext);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long firstChunkMs = -1;
        var sb = new StringBuilder(text.Length);
        try
        {
            await foreach (var part in _llmProvider.ProcessTextStreamingAsync(
                systemPrompt,
                input,
                new LlmRequestOptions { Temperature = 0 },
                ct))
            {
                if (firstChunkMs < 0) firstChunkMs = sw.ElapsedMilliseconds;
                sb.Append(part);
                context.OnCleanupChunk?.Invoke(part);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LlmCleanup] Streaming failed mid-way; returning partial ({Length} chars).", sb.Length);
        }

        var result = sb.ToString();
        _logger.LogInformation("[LlmCleanup] Cleanup done: {InLen} -> {OutLen} chars. FirstChunk={FirstMs}ms, TotalLlm={TotalMs}ms.",
            text.Length, result.Length, firstChunkMs, sw.ElapsedMilliseconds);
        return result;
    }
}
