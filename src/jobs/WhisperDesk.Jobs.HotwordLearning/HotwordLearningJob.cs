using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Llm.Contract;
using WhisperDesk.Transcript.Contract;

namespace WhisperDesk.Jobs.HotwordLearning;

public sealed class HotwordLearningJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private const int MaxEntriesPerSession = 50;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<HotwordLearningJob> _logger;
    private readonly ITranscriptionHistoryService _historyService;
    private readonly ILlmProvider _llmProvider;
    private readonly string _hotWordsFilePath;
    private readonly string _markerFilePath;
    private DateTime _lastHistoryDirWriteUtc = DateTime.MinValue;

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
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (HasHistoryChanged())
                {
                    await RunCycleAsync(stoppingToken);
                }
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

    private bool HasHistoryChanged()
    {
        var historyDir = _historyService.GetHistoryDirectory();
        if (!Directory.Exists(historyDir))
            return false;

        var dirWriteUtc = Directory.GetLastWriteTimeUtc(historyDir);
        if (dirWriteUtc <= _lastHistoryDirWriteUtc)
            return false;

        _lastHistoryDirWriteUtc = dirWriteUtc;
        return true;
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var historyDir = _historyService.GetHistoryDirectory();
        var markerTime = ReadMarkerTimestamp();

        var newFiles = Directory.GetFiles(historyDir, "*.jsonl")
            .Where(f => File.GetLastWriteTimeUtc(f) > markerTime)
            .OrderBy(f => File.GetLastWriteTimeUtc(f))
            .ToList();

        if (newFiles.Count == 0)
            return;

        var entries = new List<string>();
        var latestFileTime = markerTime;

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
                        if (entry is not null && !string.IsNullOrWhiteSpace(entry.RawText))
                        {
                            entries.Add(entry.RawText);
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HotwordLearning] Failed to read history file: {File}", file);
            }
        }

        if (entries.Count == 0)
        {
            WriteMarkerTimestamp(latestFileTime);
            return;
        }

        if (entries.Count > MaxEntriesPerSession)
            entries = entries.TakeLast(MaxEntriesPerSession).ToList();

        _logger.LogInformation("[HotwordLearning] Analyzing {Count} transcripts for hotword candidates.", entries.Count);

        var discoveredWords = await ExtractHotwordsFromSessionAsync(entries, ct);

        if (discoveredWords.Count == 0)
        {
            WriteMarkerTimestamp(latestFileTime);
            return;
        }

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

        WriteMarkerTimestamp(latestFileTime);
    }

    private async Task<List<string>> ExtractHotwordsFromSessionAsync(
        List<string> transcripts, CancellationToken ct)
    {
        var numberedTranscripts = string.Join("\n", transcripts.Select((t, i) => $"[{i + 1}] {t}"));

        const string systemPrompt = """
            You are a speech recognition improvement assistant. Analyze this sequence of transcripts from the same user session.

            Look for patterns where the user CORRECTS a previous STT misrecognition. Examples:

            1. Explicit correction: User says "不是BR，是PR" or "I said PR, not BR" — the STT misheard "PR" as "BR". Extract "PR".

            2. Repetition with context: User first says "打开那个kube内涕丝" then later says "Kubernetes集群需要更新" — the second mention reveals the correct term. Extract "Kubernetes".

            3. Spelling out: User says "就是G-R-P-C那个框架" or "gRPC，就是Google的那个RPC" — user is clarifying a term the STT might struggle with. Extract "gRPC".

            4. Domain term introduction: User says "我们用的是Terraform，T-E-R-R-A-F-O-R-M" — user spells it out because STT often gets it wrong. Extract "Terraform".

            Only extract words that the user explicitly corrected or clarified. Do NOT guess or infer — if there's no clear correction signal, return an empty array.

            Return ONLY a JSON array of words. Example: ["PR", "Kubernetes", "gRPC"]
            If no corrections found, return: []
            """;

        try
        {
            var response = await _llmProvider.ProcessTextAsync(systemPrompt, numberedTranscripts, ct: ct);

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
            _logger.LogWarning(ex, "[HotwordLearning] LLM call failed.");
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
