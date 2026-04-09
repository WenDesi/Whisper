using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.Core.Models;

namespace WhisperDesk.Core.Providers.Stt.Azure;

/// <summary>
/// Azure Speech Service streaming STT provider.
/// Receives audio via PushAudio(), emits partial/final results via events.
/// Does NOT own microphone capture -- that's AudioRouter's job.
/// </summary>
public class AzureSttProvider : IStreamingSttProvider
{
    private readonly ILogger<AzureSttProvider> _logger;
    private readonly AzureSttConfig _config;

    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;

    // Thread-safe: Recognized events fire from SDK background threads
    private ConcurrentQueue<string> _results = new();
    private TaskCompletionSource<bool>? _sessionTcs;
    private CancellationTokenRegistration? _ctRegistration;

    public string Name => "Azure Speech";

    public event EventHandler<SttPartialResult>? PartialResultReceived;
    public event EventHandler<SttFinalResult>? FinalResultReceived;
    public event EventHandler<SttError>? ErrorOccurred;

    public AzureSttProvider(ILogger<AzureSttProvider> logger, AzureSttConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task StartSessionAsync(SttSessionOptions options, CancellationToken ct = default)
    {
        using var activity = DiagnosticSources.Stt.StartActivity("AzureStt.StartSession");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("stt.provider", "Azure");

        _logger.LogInformation("[AzureStt] Starting session...");

        _results = new ConcurrentQueue<string>();
        _sessionTcs = new TaskCompletionSource<bool>();

        // Configure Speech SDK
        SpeechConfig speechConfig;
        using (var configStep = DiagnosticSources.Stt.StartActivity("AzureStt.StartSession.CreateSpeechConfig"))
        {
            configStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
            speechConfig = SpeechConfig.FromSubscription(_config.SubscriptionKey, _config.Region);
        }

        // Auto-detect languages from session options
        var languages = options.Languages.Count > 0
            ? options.Languages.ToArray()
            : new[] { "zh-CN", "en-US" };
        var autoDetectConfig = AutoDetectSourceLanguageConfig.FromLanguages(languages);
        activity?.SetTag("stt.languages", string.Join(",", languages));

        // Create push stream with matching audio format
        using (var recognizerStep = DiagnosticSources.Stt.StartActivity("AzureStt.StartSession.CreateRecognizer"))
        {
            recognizerStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);

            var audioFormat = AudioStreamFormat.GetWaveFormatPCM(
                (uint)options.AudioFormat.SampleRate,
                (byte)options.AudioFormat.BitsPerSample,
                (byte)options.AudioFormat.Channels);
            _pushStream = AudioInputStream.CreatePushStream(audioFormat);
            _audioConfig = AudioConfig.FromStreamInput(_pushStream);
            _recognizer = new SpeechRecognizer(speechConfig, autoDetectConfig, _audioConfig);
        }

        // Apply phrase hints if provider supports them
        using (var hintsStep = DiagnosticSources.Stt.StartActivity("AzureStt.StartSession.PhraseHints"))
        {
            hintsStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
            hintsStep?.SetTag("hints.count", options.PhraseHints.Count);

            if (options.PhraseHints.Count > 0)
            {
                var phraseList = PhraseListGrammar.FromRecognizer(_recognizer);
                foreach (var hint in options.PhraseHints)
                {
                    phraseList.AddPhrase(hint);
                }
                _logger.LogInformation("[AzureStt] Added {Count} phrase hints.", options.PhraseHints.Count);
            }
        }

        // Wire events
        _recognizer.Recognizing += (_, e) =>
        {
            _logger.LogDebug("[AzureStt] Partial: {Text}", e.Result.Text);
            PartialResultReceived?.Invoke(this, new SttPartialResult(e.Result.Text));
        };

        _recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                _logger.LogInformation("[AzureStt] Final segment: {Text}", e.Result.Text);
                _results.Enqueue(e.Result.Text);
                FinalResultReceived?.Invoke(this, new SttFinalResult(
                    e.Result.Text,
                    TimeSpan.FromTicks(e.Result.OffsetInTicks),
                    e.Result.Duration));
            }
            else if (e.Result.Reason == ResultReason.NoMatch)
            {
                _logger.LogWarning("[AzureStt] NoMatch -- speech not recognized in segment.");
            }
        };

        _recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                _logger.LogError("[AzureStt] Error: {Code} -- {Details}", e.ErrorCode, e.ErrorDetails);
                ErrorOccurred?.Invoke(this, new SttError(e.ErrorCode.ToString(), e.ErrorDetails));
            }
            else
            {
                _logger.LogInformation("[AzureStt] Canceled: {Reason}", e.Reason);
            }
            _sessionTcs?.TrySetResult(true);
        };

        _recognizer.SessionStopped += (_, _) =>
        {
            _logger.LogDebug("[AzureStt] Session stopped.");
            _sessionTcs?.TrySetResult(true);
        };

        _ctRegistration = ct.Register(() => _sessionTcs?.TrySetCanceled());

        using (var startRecogStep = DiagnosticSources.Stt.StartActivity("AzureStt.StartSession.StartContinuousRecognition"))
        {
            startRecogStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
            await _recognizer.StartContinuousRecognitionAsync();
        }

        _logger.LogInformation("[AzureStt] Session started. Ready for audio.");
    }

    public void PushAudio(ReadOnlyMemory<byte> audioData)
    {
        _pushStream?.Write(audioData.ToArray());
    }

    public void SignalEndOfAudio()
    {
        using var activity = DiagnosticSources.Stt.StartActivity("AzureStt.SignalEndOfAudio");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("stt.provider", "Azure");

        _pushStream?.Close();
        _logger.LogInformation("[AzureStt] End of audio signaled.");
    }

    public async Task<string> EndSessionAsync()
    {
        using var activity = DiagnosticSources.Stt.StartActivity("AzureStt.EndSession");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("stt.provider", "Azure");

        _logger.LogInformation("[AzureStt] Ending session...");

        // Dispose cancellation token registration
        _ctRegistration?.Dispose();
        _ctRegistration = null;

        if (_recognizer != null)
        {
            using (var stopStep = DiagnosticSources.Stt.StartActivity("AzureStt.EndSession.StopContinuousRecognition"))
            {
                stopStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                await _recognizer.StopContinuousRecognitionAsync();
            }

            using (var waitStep = DiagnosticSources.Stt.StartActivity("AzureStt.EndSession.WaitForCompletion"))
            {
                waitStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                if (_sessionTcs != null)
                {
                    var completedTask = await Task.WhenAny(_sessionTcs.Task, Task.Delay(3000));
                    waitStep?.SetTag("timed_out", completedTask != _sessionTcs.Task);
                }
            }

            _recognizer.Dispose();
            _recognizer = null;
        }

        _audioConfig?.Dispose();
        _audioConfig = null;
        _pushStream = null;

        var segments = _results.ToArray();
        var fullText = string.Join("", segments);
        _logger.LogInformation("[AzureStt] Session ended. {Length} chars from {Segments} segments.",
            fullText.Length, segments.Length);

        activity?.SetTag("transcript.length", fullText.Length);
        activity?.SetTag("transcript.segments", segments.Length);

        return fullText;
    }

    public void Dispose()
    {
        _pushStream?.Close();
        _recognizer?.Dispose();
        _audioConfig?.Dispose();
        GC.SuppressFinalize(this);
    }
}
