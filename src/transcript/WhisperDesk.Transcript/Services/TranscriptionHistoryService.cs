using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhisperDesk.Transcript.Models;

namespace WhisperDesk.Transcript.Services;

public class TranscriptionHistoryService
{
    private readonly ILogger<TranscriptionHistoryService> _logger;
    private readonly string _historyDir;
    private readonly TimeSpan _sessionGap;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TranscriptionHistoryService(
        ILogger<TranscriptionHistoryService> logger,
        TimeSpan? sessionGap = null)
    {
        _logger = logger;
        _sessionGap = sessionGap ?? TimeSpan.FromMinutes(10);

        _historyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "history");
        Directory.CreateDirectory(_historyDir);
    }

    public async Task WriteEntryAsync(TranscriptionHistoryEntry entry)
    {
        await _writeLock.WaitAsync();
        try
        {
            var targetFile = ResolveTargetFile(entry.Timestamp);
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            await File.AppendAllTextAsync(targetFile, line + "\n");
            _logger.LogDebug("History entry {Id} written to {File}", entry.Id, Path.GetFileName(targetFile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write history entry {Id}", entry.Id);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string ResolveTargetFile(DateTime now)
    {
        var latest = Directory.GetFiles(_historyDir, "*.jsonl")
            .OrderDescending()
            .FirstOrDefault();

        if (latest != null)
        {
            var lastEntry = ReadLastTimestamp(latest);
            if (lastEntry.HasValue && (now - lastEntry.Value) < _sessionGap)
                return latest;
        }

        var fileName = now.ToString("yyyy-MM-ddTHH-mm-ss") + ".jsonl";
        return Path.Combine(_historyDir, fileName);
    }

    private static DateTime? ReadLastTimestamp(string filePath)
    {
        try
        {
            string? lastLine = null;
            foreach (var line in File.ReadLines(filePath))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lastLine = line;
            }

            if (lastLine == null) return null;

            using var doc = JsonDocument.Parse(lastLine);
            if (doc.RootElement.TryGetProperty("timestamp", out var ts))
                return ts.GetDateTime();
        }
        catch
        {
            // Corrupted file — start a new session
        }

        return null;
    }

    public string GetHistoryDirectory() => _historyDir;
}
