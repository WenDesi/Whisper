using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.CoreAudioApi;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// Speech-to-text using Azure AI Speech Service with direct microphone input.
/// Supports continuous recognition with Chinese + English code-switching.
/// </summary>
public class AzureSpeechService : ISpeechToTextService, IDisposable
{
    private readonly ILogger<AzureSpeechService> _logger;
    private readonly AzureSpeechSettings _settings;

    private SpeechRecognizer? _recognizer;
    private AudioConfig? _audioConfig;
    private List<string> _results = new();
    private TaskCompletionSource<bool>? _sessionTcs;

    public AzureSpeechService(ILogger<AzureSpeechService> logger, AzureSpeechSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task StartListeningAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[AzureSpeech] StartListening — setting up recognizer...");
        _logger.LogDebug("[AzureSpeech] Region: {Region}", _settings.Region);

        // Log available microphones and default device
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _logger.LogInformation("[AzureSpeech] Default microphone: {Name} (Volume: {Volume:P0})",
                defaultDevice.FriendlyName, defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar);

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in devices)
            {
                _logger.LogDebug("[AzureSpeech] Available mic: {Name}{Default}",
                    device.FriendlyName,
                    device.ID == defaultDevice.ID ? " [DEFAULT]" : "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AzureSpeech] Could not enumerate microphones");
        }

        var speechConfig = SpeechConfig.FromSubscription(
            _settings.SubscriptionKey,
            _settings.Region);

        // Enable auto language detection for Chinese + English mixed speech
        var autoDetectConfig = AutoDetectSourceLanguageConfig.FromLanguages(
            new[] { "zh-CN", "en-US" });

        // Use default microphone directly — no NAudio, no temp files
        _audioConfig = AudioConfig.FromDefaultMicrophoneInput();
        _recognizer = new SpeechRecognizer(speechConfig, autoDetectConfig, _audioConfig);

        _results = new List<string>();
        _sessionTcs = new TaskCompletionSource<bool>();

        _recognizer.Recognizing += (_, e) =>
        {
            _logger.LogDebug("[AzureSpeech] Recognizing (partial): {Text}", e.Result.Text);
        };

        _recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                _logger.LogInformation("[AzureSpeech] Recognized segment: {Text}", e.Result.Text);
                _results.Add(e.Result.Text);
            }
            else if (e.Result.Reason == ResultReason.NoMatch)
            {
                _logger.LogWarning("[AzureSpeech] NoMatch — speech could not be recognized in this segment");
            }
        };

        _recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                _logger.LogError("[AzureSpeech] Recognition FAILED: ErrorCode={ErrorCode}, Details={ErrorDetails}",
                    e.ErrorCode, e.ErrorDetails);
            }
            else
            {
                _logger.LogInformation("[AzureSpeech] Recognition canceled: Reason={Reason}", e.Reason);
            }
            _sessionTcs?.TrySetResult(true);
        };

        _recognizer.SessionStarted += (_, _) =>
        {
            _logger.LogDebug("[AzureSpeech] Session started");
        };

        _recognizer.SessionStopped += (_, _) =>
        {
            _logger.LogDebug("[AzureSpeech] Session stopped");
            _sessionTcs?.TrySetResult(true);
        };

        ct.Register(() => _sessionTcs?.TrySetCanceled());

        await _recognizer.StartContinuousRecognitionAsync();
        _logger.LogInformation("[AzureSpeech] Now listening from microphone...");
    }

    public async Task<string> StopListeningAsync()
    {
        _logger.LogInformation("[AzureSpeech] StopListening requested...");

        if (_recognizer != null)
        {
            await _recognizer.StopContinuousRecognitionAsync();

            // Wait briefly for final results to arrive
            if (_sessionTcs != null)
            {
                await Task.WhenAny(_sessionTcs.Task, Task.Delay(3000));
            }

            _recognizer.Dispose();
            _recognizer = null;
        }

        _audioConfig?.Dispose();
        _audioConfig = null;

        var fullText = string.Join("", _results);
        _logger.LogInformation("[AzureSpeech] Transcription complete: {Length} chars from {Segments} segments",
            fullText.Length, _results.Count);

        if (fullText.Length == 0)
        {
            _logger.LogWarning("[AzureSpeech] No speech recognized. Check microphone input.");
        }
        else
        {
            _logger.LogInformation("[AzureSpeech] Full transcription: {Text}", fullText);
        }

        return fullText;
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _audioConfig?.Dispose();
        GC.SuppressFinalize(this);
    }
}
