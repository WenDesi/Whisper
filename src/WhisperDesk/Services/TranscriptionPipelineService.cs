using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

public class TranscriptionPipelineService
{
    private readonly ILogger<TranscriptionPipelineService> _logger;
    private readonly ISpeechToTextService _sttService;
    private readonly ITextCleanupService _cleanupService;
    private readonly TranscriptionLogService _logService;

    public event EventHandler<AppStatus>? StatusChanged;
    public event EventHandler<TranscriptionResult>? TranscriptionCompleted;
    public event EventHandler<string>? ErrorOccurred;

    public string? LastCleanedText { get; private set; }

    public TranscriptionPipelineService(
        ILogger<TranscriptionPipelineService> logger,
        ISpeechToTextService sttService,
        ITextCleanupService cleanupService,
        TranscriptionLogService logService)
    {
        _logger = logger;
        _sttService = sttService;
        _cleanupService = cleanupService;
        _logService = logService;
    }

    public async void StartRecording()
    {
        try
        {
            _logger.LogInformation("[Pipeline] StartRecording — starting STT listening...");
            // Show Listening status IMMEDIATELY — don't wait for SDK
            StatusChanged?.Invoke(this, AppStatus.Listening);
            await _sttService.StartListeningAsync();
            _logger.LogInformation("[Pipeline] Now listening for speech...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline] Failed to start listening");
            StatusChanged?.Invoke(this, AppStatus.Error);
            ErrorOccurred?.Invoke(this, $"Failed to start listening: {ex.Message}");
        }
    }

    public async Task StopRecordingAndProcessAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[Pipeline] StopRecording — stopping STT and getting results...");
            StatusChanged?.Invoke(this, AppStatus.Transcribing);

            var rawText = await _sttService.StopListeningAsync();

            if (string.IsNullOrWhiteSpace(rawText))
            {
                _logger.LogWarning("[Pipeline] STT returned empty text — nothing to process.");
                StatusChanged?.Invoke(this, AppStatus.Idle);
                return;
            }

            _logger.LogInformation("[Pipeline] Raw transcription ({Length} chars): {Text}",
                rawText.Length, rawText);

            // Clean up via configured cleanup provider
            _logger.LogInformation("[Pipeline] Sending to text cleanup ({Provider})...",
                _cleanupService.GetType().Name);
            StatusChanged?.Invoke(this, AppStatus.Cleaning);
            var cleanedText = await _cleanupService.CleanupTextAsync(rawText, ct);

            _logger.LogInformation("[Pipeline] Cleaned text ({Length} chars): {Text}",
                cleanedText.Length, cleanedText);

            // Store result
            LastCleanedText = cleanedText;

            var result = new TranscriptionResult
            {
                RawText = rawText,
                CleanedText = cleanedText,
                AudioDuration = TimeSpan.Zero, // duration not tracked in direct mic mode
                SourceFile = null
            };

            // Copy to clipboard
            _logger.LogDebug("[Pipeline] Copying cleaned text to clipboard...");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Clipboard.SetText(cleanedText);
            });

            // Log to file
            await _logService.LogTranscriptionAsync(result);

            // Notify
            TranscriptionCompleted?.Invoke(this, result);
            StatusChanged?.Invoke(this, AppStatus.Ready);

            _logger.LogInformation("[Pipeline] Complete. Cleaned text copied to clipboard.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline] Failed to process recording");
            StatusChanged?.Invoke(this, AppStatus.Error);
            ErrorOccurred?.Invoke(this, $"Processing failed: {ex.Message}");
        }
    }

    public Task ProcessFileAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogWarning("[Pipeline] File processing not supported in direct microphone mode. File: {FilePath}", filePath);
        return Task.CompletedTask;
    }
}
