using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

public class TranscriptionPipelineService
{
    private readonly ILogger<TranscriptionPipelineService> _logger;
    private readonly AudioRecorderService _recorder;
    private readonly ISpeechToTextService _sttService;
    private readonly ITextCleanupService _cleanupService;
    private readonly TranscriptionLogService _logService;

    public event EventHandler<AppStatus>? StatusChanged;
    public event EventHandler<TranscriptionResult>? TranscriptionCompleted;
    public event EventHandler<string>? ErrorOccurred;

    public string? LastCleanedText { get; private set; }

    public TranscriptionPipelineService(
        ILogger<TranscriptionPipelineService> logger,
        AudioRecorderService recorder,
        ISpeechToTextService sttService,
        ITextCleanupService cleanupService,
        TranscriptionLogService logService)
    {
        _logger = logger;
        _recorder = recorder;
        _sttService = sttService;
        _cleanupService = cleanupService;
        _logService = logService;
    }

    public void StartRecording()
    {
        try
        {
            _recorder.StartRecording();
            StatusChanged?.Invoke(this, AppStatus.Listening);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            StatusChanged?.Invoke(this, AppStatus.Error);
            ErrorOccurred?.Invoke(this, $"Failed to start recording: {ex.Message}");
        }
    }

    public async Task StopRecordingAndProcessAsync(CancellationToken ct = default)
    {
        try
        {
            var audioData = _recorder.StopRecording();
            if (audioData.Length == 0)
            {
                StatusChanged?.Invoke(this, AppStatus.Idle);
                return;
            }

            await ProcessAudioAsync(audioData, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process recording");
            StatusChanged?.Invoke(this, AppStatus.Error);
            ErrorOccurred?.Invoke(this, $"Processing failed: {ex.Message}");
        }
    }

    public async Task ProcessFileAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Processing file: {FilePath}", filePath);
            var audioData = AudioRecorderService.LoadAudioFile(filePath);
            await ProcessAudioAsync(audioData, filePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process file: {FilePath}", filePath);
            StatusChanged?.Invoke(this, AppStatus.Error);
            ErrorOccurred?.Invoke(this, $"File processing failed: {ex.Message}");
        }
    }

    private async Task ProcessAudioAsync(byte[] audioData, string? sourceFile = null, CancellationToken ct = default)
    {
        // Step 1: Transcribe via configured STT provider
        StatusChanged?.Invoke(this, AppStatus.Transcribing);
        var rawText = await _sttService.TranscribeAsync(audioData, ct: ct);

        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("Transcription returned empty text");
            StatusChanged?.Invoke(this, AppStatus.Idle);
            return;
        }

        // Step 2: Clean up via configured cleanup provider
        StatusChanged?.Invoke(this, AppStatus.Cleaning);
        var cleanedText = await _cleanupService.CleanupTextAsync(rawText, ct);

        // Step 3: Store result
        LastCleanedText = cleanedText;

        var result = new TranscriptionResult
        {
            RawText = rawText,
            CleanedText = cleanedText,
            AudioDuration = TimeSpan.FromSeconds(audioData.Length / (16000.0 * 2)), // 16kHz, 16-bit
            SourceFile = sourceFile
        };

        // Step 4: Copy to clipboard
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Windows.Clipboard.SetText(cleanedText);
        });

        // Step 5: Log
        await _logService.LogTranscriptionAsync(result);

        // Step 6: Notify
        TranscriptionCompleted?.Invoke(this, result);
        StatusChanged?.Invoke(this, AppStatus.Ready);

        _logger.LogInformation("Pipeline complete. Cleaned text copied to clipboard.");
    }
}
