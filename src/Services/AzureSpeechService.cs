using System.Collections.Concurrent;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.Wave;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// Speech-to-text using Azure AI Speech Service with PushAudioInputStream.
/// Starts NAudio microphone capture immediately, buffers audio while SDK connects,
/// then flushes buffer + continues streaming — zero audio loss.
/// </summary>
public class AzureSpeechService : ISpeechToTextService, IDisposable
{
    private readonly ILogger<AzureSpeechService> _logger;
    private readonly AzureSpeechSettings _settings;

    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private WaveInEvent? _waveIn;

    private List<string> _results = new();
    private TaskCompletionSource<bool>? _sessionTcs;

    // Buffer for audio captured before SDK is ready
    private readonly ConcurrentQueue<byte[]> _audioBuffer = new();
    private volatile bool _sdkReady;

    public AzureSpeechService(ILogger<AzureSpeechService> logger, AzureSpeechSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task StartListeningAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[AzureSpeech] StartListening — starting mic capture + SDK setup in parallel...");

        _sdkReady = false;
        _results = new List<string>();
        _sessionTcs = new TaskCompletionSource<bool>();

        // 1. Start microphone capture IMMEDIATELY via NAudio
        StartMicCapture();
        _logger.LogInformation("[AzureSpeech] Microphone capture started (buffering audio)...");

        // 2. Set up Azure Speech SDK in parallel (this takes ~500ms)
        var speechConfig = SpeechConfig.FromSubscription(
            _settings.SubscriptionKey,
            _settings.Region);

        var autoDetectConfig = AutoDetectSourceLanguageConfig.FromLanguages(
            new[] { "zh-CN", "en-US" });

        // Use PushAudioInputStream so we control what audio goes in
        var audioFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(audioFormat);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);
        _recognizer = new SpeechRecognizer(speechConfig, autoDetectConfig, _audioConfig);

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

        // 3. SDK is now ready — flush buffered audio
        _sdkReady = true;
        FlushBufferedAudio();

        _logger.LogInformation("[AzureSpeech] SDK ready. Buffered audio flushed. Now streaming live.");
    }

    public async Task<string> StopListeningAsync()
    {
        _logger.LogInformation("[AzureSpeech] StopListening requested...");

        // Stop microphone capture first
        StopMicCapture();

        // Close the push stream to signal end of audio
        _pushStream?.Close();

        if (_recognizer != null)
        {
            await _recognizer.StopContinuousRecognitionAsync();

            if (_sessionTcs != null)
            {
                await Task.WhenAny(_sessionTcs.Task, Task.Delay(3000));
            }

            _recognizer.Dispose();
            _recognizer = null;
        }

        _audioConfig?.Dispose();
        _audioConfig = null;
        _pushStream = null;

        var fullText = string.Join("", _results);
        _logger.LogInformation("[AzureSpeech] Transcription complete: {Length} chars from {Segments} segments",
            fullText.Length, _results.Count);

        if (fullText.Length == 0)
        {
            _logger.LogWarning("[AzureSpeech] No speech recognized.");
        }
        else
        {
            _logger.LogInformation("[AzureSpeech] Full transcription: {Text}", fullText);
        }

        return fullText;
    }

    private void StartMicCapture()
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += OnMicDataAvailable;
        _waveIn.StartRecording();
    }

    private void StopMicCapture()
    {
        if (_waveIn != null)
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnMicDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        if (_sdkReady)
        {
            // SDK is ready — push directly
            _pushStream?.Write(chunk);
        }
        else
        {
            // SDK not ready yet — buffer for later
            _audioBuffer.Enqueue(chunk);
        }
    }

    private void FlushBufferedAudio()
    {
        int flushedChunks = 0;
        while (_audioBuffer.TryDequeue(out var chunk))
        {
            _pushStream?.Write(chunk);
            flushedChunks++;
        }

        if (flushedChunks > 0)
        {
            _logger.LogInformation("[AzureSpeech] Flushed {Count} buffered audio chunks to SDK", flushedChunks);
        }
    }

    public void Dispose()
    {
        StopMicCapture();
        _pushStream?.Close();
        _recognizer?.Dispose();
        _audioConfig?.Dispose();
        GC.SuppressFinalize(this);
    }
}
