using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// GPT text cleanup service via Azure OpenAI-compatible endpoint.
/// Uses OpenAI SDK with ApiKeyCredential + custom endpoint.
/// </summary>
public class AzureOpenAIService : ITextCleanupService
{
    private readonly ILogger<AzureOpenAIService> _logger;
    private readonly AzureOpenAISettings _settings;

    public AzureOpenAIService(ILogger<AzureOpenAIService> logger, AzureOpenAISettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task<string> CleanupTextAsync(string rawText, CancellationToken ct = default)
    {
        _logger.LogInformation("[TextCleanup] Starting cleanup via {Model}, input ({Length} chars): {Text}",
            _settings.ChatDeployment, rawText.Length, rawText);
        _logger.LogDebug("[TextCleanup] Endpoint: {Endpoint}", _settings.Endpoint);

        var chatClient = new ChatClient(
            credential: new ApiKeyCredential(_settings.ApiKey),
            model: _settings.ChatDeployment,
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(_settings.Endpoint)
            });

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are a transcription cleanup assistant. Your job is to:
                1. Remove filler words (umm, uh, 嗯, 啊, 那个, 就是, 然后 etc.)
                2. Fix grammar and punctuation
                3. Keep the original meaning and tone intact
                4. Preserve all technical terms exactly as spoken (e.g., Redis, Kubernetes, API, Docker)
                5. Keep the original language - if Chinese, output Chinese; if English, output English
                6. For mixed language, keep technical English terms within Chinese text
                7. Do NOT translate, summarize, or add content
                8. Output ONLY the cleaned text, nothing else
                """),
            new UserChatMessage(rawText)
        };

        _logger.LogDebug("[TextCleanup] Sending request to {Model}...", _settings.ChatDeployment);
        var response = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
        var cleanedText = response.Value.Content[0].Text;

        _logger.LogInformation("[TextCleanup] Cleanup complete: {RawLen} -> {CleanLen} chars", rawText.Length, cleanedText.Length);
        _logger.LogInformation("[TextCleanup] Output: {Text}", cleanedText);

        return cleanedText;
    }
}
