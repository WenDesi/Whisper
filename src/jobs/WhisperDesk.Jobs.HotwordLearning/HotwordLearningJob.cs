using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Llm.Contract;
using WhisperDesk.Transcript.Contract;

namespace WhisperDesk.Jobs.HotwordLearning;

/// <summary>
/// Background job that periodically analyzes transcription history to discover
/// new hotwords by comparing raw STT output with LLM-corrected text.
/// </summary>
public sealed class HotwordLearningJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const int MaxEntriesPerBatch = 20;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<HotwordLearningJob> _logger;
    private readonly ITranscriptionHistoryService _historyService;
    private readonly ILlmProvider _llmProvider;
    private readonly string _hotWordsFilePath;
    private readonly string _markerFilePath;

    public HotwordLearningJob(
        ILogger<HotwordLearningJob> logger,
        ITranscriptionHistoryService historyService,
        ILlmProvider llmProvider,
        PipelineConfig config)
    {
        _logger = logger;
        _historyService = historyService;
        _llmProvider = llmProvider;

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;
        _hotWordsFilePath = Path.Combine(exeDir, config.HotWordsFile);
        _markerFilePath = Path.Combine(exeDir, "hotwords-processed.marker");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial run to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HotwordLearning] Cycle failed. Will retry next interval.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var historyDir = _historyService.GetHistoryDirectory();
        if (!Directory.Exists(historyDir))
        {
            _logger.LogDebug("[HotwordLearning] History directory not found: {Dir}", historyDir);
            return;
        }

        var markerTime = ReadMarkerTimestamp();

        // Find JSONL files newer than the marker
        var newFiles = Directory.GetFiles(historyDir, "*.jsonl")
            .Where(f => File.GetLastWriteTimeUtc(f) > markerTime)
            .OrderBy(f => File.GetLastWriteTimeUtc(f))
            .ToList();

        if (newFiles.Count == 0)
        {
            _logger.LogDebug("[HotwordLearning] No new history files to process.");
            return;
        }

        // Collect differing pairs from new files
        var pairs = new List<(string Raw, string Processed)>();
        DateTime latestFileTime = markerTime;

        foreach (var file in newFiles)
        {
            var fileTime = File.GetLastWriteTimeUtc(file);
            if (fileTime > latestFileTime) latestFileTime = fileTime;

            try
            {
                var lines = await File.ReadAllLinesAsync(file, ct);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<TranscriptionHistoryEntry>(line, CamelCase);
                        if (entry is not null
                            && !string.IsNullOrWhiteSpace(entry.RawText)
                            && !string.IsNullOrWhiteSpace(entry.ProcessedText)
                            && !string.Equals(entry.RawText, entry.ProcessedText, StringComparison.Ordinal))
                        {
                            pairs.Add((entry.RawText, entry.ProcessedText));
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed lines
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HotwordLearning] Failed to read history file: {File}", file);
            }
        }

        if (pairs.Count == 0)
        {
            _logger.LogDebug("[HotwordLearning] No differing pairs found in new history files.");
            WriteMarkerTimestamp(latestFileTime);
            return;
        }

        _logger.LogInformation("[HotwordLearning] Found {Count} differing pairs across {Files} files.",
            pairs.Count, newFiles.Count);

        // Process in batches
        var discoveredWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < pairs.Count; i += MaxEntriesPerBatch)
        {
            var batch = pairs.Skip(i).Take(MaxEntriesPerBatch).ToList();
            var words = await ExtractHotwordsFromBatchAsync(batch, ct);
            foreach (var w in words) discoveredWords.Add(w);
        }

        if (discoveredWords.Count == 0)
        {
            _logger.LogDebug("[HotwordLearning] LLM did not suggest any new hotwords.");
            WriteMarkerTimestamp(latestFileTime);
            return;
        }

        // Merge with existing hotwords
        var existing = await LoadExistingHotwordsAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var newWords = discoveredWords.Where(w => !existingSet.Contains(w)).ToList();

        if (newWords.Count > 0)
        {
            var merged = existing.Concat(newWords).ToList();
            await WriteHotwordsAtomicAsync(merged, ct);
            _logger.LogInformation("[HotwordLearning] Added {Count} new hotwords: {Words}",
                newWords.Count, string.Join(", ", newWords));
        }
        else
        {
            _logger.LogDebug("[HotwordLearning] All suggested words already exist.");
        }

        WriteMarkerTimestamp(latestFileTime);
    }

    private async Task<List<string>> ExtractHotwordsFromBatchAsync(
        List<(string Raw, string Processed)> batch, CancellationToken ct)
    {
        var pairsText = string.Join("\n", batch.Select((p, i) =>
            $"{i + 1}. Raw: \"{p.Raw}\"\n   Corrected: \"{p.Processed}\""));

        const string systemPrompt =
            "You are a speech recognition improvement assistant. " +
            "Compare these raw STT outputs with the corrected versions. " +
            "Extract proper nouns, technical terms, or specific words that the STT misrecognized. " +
            "Return ONLY a JSON array of words that should be added as hotwords. " +
            "Example: [\"Kubernetes\", \"gRPC\", \"Redis\"]";

        try
        {
            var response = await _llmProvider.ProcessTextAsync(systemPrompt, pairsText, ct: ct);

            // Extract JSON array from response (handle markdown code blocks)
            var jsonStart = response.IndexOf('[');
            var jsonEnd = response.LastIndexOf(']');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return [];

            var jsonArray = response[jsonStart..(jsonEnd + 1)];
            var words = JsonSerializer.Deserialize<List<string>>(jsonArray);

            return words?
                .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length <= 100)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HotwordLearning] LLM call failed for batch.");
            return [];
        }
    }

    private async Task<List<string>> LoadExistingHotwordsAsync(CancellationToken ct)
    {
        if (!File.Exists(_hotWordsFilePath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(_hotWordsFilePath, ct);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("hotwords", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HotwordLearning] Failed to read existing hotwords.");
        }

        return [];
    }

    private async Task WriteHotwordsAtomicAsync(List<string> words, CancellationToken ct)
    {
        var obj = new { hotwords = words };
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });

        var tempPath = _hotWordsFilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, _hotWordsFilePath, overwrite: true);
    }

    private DateTime ReadMarkerTimestamp()
    {
        if (!File.Exists(_markerFilePath))
            return DateTime.MinValue;

        try
        {
            var text = File.ReadAllText(_markerFilePath).Trim();
            return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void WriteMarkerTimestamp(DateTime utc)
    {
        try
        {
            File.WriteAllText(_markerFilePath, utc.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HotwordLearning] Failed to write marker file.");
        }
    }
}
