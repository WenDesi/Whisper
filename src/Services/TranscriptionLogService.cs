using System.IO;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

public class TranscriptionLogService
{
    private readonly ILogger<TranscriptionLogService> _logger;
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TranscriptionLogService(ILogger<TranscriptionLogService> logger, TranscriptionSettings settings)
    {
        _logger = logger;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk");

        Directory.CreateDirectory(appDataDir);
        _logFilePath = Path.Combine(appDataDir, settings.LogFile);
    }

    public async Task LogTranscriptionAsync(TranscriptionResult result)
    {
        await _writeLock.WaitAsync();
        try
        {
            var logEntry = $"""
                === [{result.Timestamp:yyyy-MM-dd HH:mm:ss}] ===
                Duration: {result.AudioDuration:mm\:ss}
                Source: {result.SourceFile ?? "microphone"}
                Language: {result.Language}

                --- Raw ---
                {result.RawText}

                --- Cleaned ---
                {result.CleanedText}

                """;

            await File.AppendAllTextAsync(_logFilePath, logEntry);
            _logger.LogDebug("Transcription logged to {LogFile}", _logFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write transcription log");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public string GetLogFilePath() => _logFilePath;

    public async Task<string> ReadLogAsync()
    {
        if (!File.Exists(_logFilePath)) return string.Empty;
        return await File.ReadAllTextAsync(_logFilePath);
    }
}
