using System.IO;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// Azure OpenAI Whisper for STT + GPT for text cleanup.
/// Can serve as both ISpeechToTextService and ITextCleanupService.
/// </summary>
public class AzureOpenAIService : ISpeechToTextService, ITextCleanupService
{
    private readonly ILogger<AzureOpenAIService> _logger;
    private readonly AzureOpenAISettings _settings;
    private readonly AzureOpenAIClient _client;

    public AzureOpenAIService(ILogger<AzureOpenAIService> logger, AzureOpenAISettings settings)
    {
        _logger = logger;
        _settings = settings;
        _client = new AzureOpenAIClient(
            new Uri(settings.Endpoint),
            new AzureKeyCredential(settings.ApiKey));
    }

    public async Task<string> TranscribeAsync(byte[] audioData, string? language = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Transcribing audio ({Size} bytes) via Azure OpenAI Whisper...", audioData.Length);

        var audioClient = _client.GetAudioClient(_settings.WhisperDeployment);

        using var audioStream = new MemoryStream(audioData);

        var options = new AudioTranscriptionOptions
        {
            Language = language ?? "zh",
            Prompt = "This audio contains Chinese speech with occasional English technical terms.",
            ResponseFormat = AudioTranscriptionFormat.Text
        };

        var result = await audioClient.TranscribeAudioAsync(
            audioStream,
            "recording.wav",
            options,
            ct);

        var text = result.Value.Text;
        _logger.LogInformation("Transcription complete: {Length} chars", text.Length);

        return text;
    }

    public async Task<string> CleanupTextAsync(string rawText, CancellationToken ct = default)
    {
        _logger.LogInformation("Cleaning up transcription text via Azure OpenAI...");

        var chatClient = _client.GetChatClient(_settings.ChatDeployment);

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

        var response = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
        var cleanedText = response.Value.Content[0].Text;

        _logger.LogInformation("Cleanup complete: {RawLen} -> {CleanLen} chars",
            rawText.Length, cleanedText.Length);

        return cleanedText;
    }
}
