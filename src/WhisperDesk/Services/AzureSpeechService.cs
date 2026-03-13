using System.IO;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// Speech-to-text using Azure AI Speech Service (Cognitive Services SDK).
/// Supports continuous recognition with Chinese + English code-switching.
/// </summary>
public class AzureSpeechService : ISpeechToTextService, IDisposable
{
    private readonly ILogger<AzureSpeechService> _logger;
    private readonly AzureSpeechSettings _settings;

    public AzureSpeechService(ILogger<AzureSpeechService> logger, AzureSpeechSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task<string> TranscribeAsync(byte[] audioData, string? language = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Transcribing audio ({Size} bytes) via Azure Speech Service...", audioData.Length);

        var speechConfig = SpeechConfig.FromEndpoint(
            new Uri(_settings.Endpoint),
            _settings.SubscriptionKey);

        // Set primary language
        var primaryLanguage = language ?? _settings.Language;

        // Enable auto language detection for Chinese + English mixed speech
        var autoDetectConfig = AutoDetectSourceLanguageConfig.FromLanguages(
            new[] { "zh-CN", "en-US" });

        // Configure for better accuracy with technical terms
        speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "5000");
        speechConfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "1000");

        // Write audio data to a temporary WAV file for the SDK
        var tempFile = Path.GetTempFileName() + ".wav";
        try
        {
            await File.WriteAllBytesAsync(tempFile, audioData, ct);

            using var audioConfig = AudioConfig.FromWavFileInput(tempFile);
            using var recognizer = new SpeechRecognizer(speechConfig, autoDetectConfig, audioConfig);

            // Collect all recognized segments
            var results = new List<string>();
            var tcs = new TaskCompletionSource<bool>();

            recognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                {
                    _logger.LogDebug("Recognized segment: {Text}", e.Result.Text);
                    results.Add(e.Result.Text);
                }
            };

            recognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                {
                    _logger.LogError("Speech recognition error: {ErrorCode} - {ErrorDetails}",
                        e.ErrorCode, e.ErrorDetails);
                }
                tcs.TrySetResult(true);
            };

            recognizer.SessionStopped += (_, _) =>
            {
                _logger.LogDebug("Speech recognition session stopped");
                tcs.TrySetResult(true);
            };

            // Register cancellation
            ct.Register(() => tcs.TrySetCanceled());

            await recognizer.StartContinuousRecognitionAsync();
            await tcs.Task;
            await recognizer.StopContinuousRecognitionAsync();

            var fullText = string.Join("", results);
            _logger.LogInformation("Transcription complete: {Length} chars from {Segments} segments",
                fullText.Length, results.Count);

            return fullText;
        }
        finally
        {
            // Clean up temp file
            try { File.Delete(tempFile); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
