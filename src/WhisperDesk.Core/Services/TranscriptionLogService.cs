using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.Core.Models;

namespace WhisperDesk.Core.Services;

/// <summary>
/// Thread-safe transcription logging to file.
/// </summary>
public class TranscriptionLogService
{
    private readonly ILogger<TranscriptionLogService> _logger;
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TranscriptionLogService(ILogger<TranscriptionLogService> logger, string logFileName = "transcription-history.log")
    {
        _logger = logger;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk");
        Directory.CreateDirectory(appDataDir);
        _logFilePath = Path.Combine(appDataDir, logFileName);
    }

    public async Task LogTranscriptionAsync(PipelineResult result)
    {
        using var activity = DiagnosticSources.Pipeline.StartActivity("TranscriptionLog.LogTranscription");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        using (var lockStep = DiagnosticSources.Pipeline.StartActivity("TranscriptionLog.LogTranscription.WaitLock"))
        {
            lockStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
            await _writeLock.WaitAsync();
        }

        try
        {
            using (var writeStep = DiagnosticSources.Pipeline.StartActivity("TranscriptionLog.LogTranscription.FileWrite"))
            {
                writeStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                writeStep?.SetTag("log.file", _logFilePath);

                var logEntry = $"""
                    === [{result.Timestamp:yyyy-MM-dd HH:mm:ss}] ===
                    Duration: {result.AudioDuration:mm\:ss}
                    Source: {result.SourceFile ?? "microphone"}
                    Language: {result.Language}

                    --- Raw ---
                    {result.RawTranscript}

                    --- Processed ---
                    {result.ProcessedText}

                    """;

                await File.AppendAllTextAsync(_logFilePath, logEntry);
                _logger.LogDebug("Transcription logged to {LogFile}", _logFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write transcription log");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
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
