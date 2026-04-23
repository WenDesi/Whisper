using System.Reflection;
using System.Text.Json;
using Fluid;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Llm.Contract;
using WhisperDesk.Transcript.Contract;

namespace WhisperDesk.Jobs.HotwordLearning;

public sealed class HotwordLearningJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContextWindow = TimeSpan.FromSeconds(30);
    private const int MaxEntriesPerSession = 50;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly FluidParser Parser = new();
    private readonly IFluidTemplate _promptTemplate;

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

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk");
        Directory.CreateDirectory(appDataDir);
        _hotWordsFilePath = Path.Combine(appDataDir, config.HotWordsFile);
        _markerFilePath = Path.Combine(appDataDir, "hotwords-processed.marker");

        _promptTemplate = LoadPromptTemplate("ExtractHotwords.liquid");
    }

    private static IFluidTemplate LoadPromptTemplate(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"WhisperDesk.Jobs.HotwordLearning.Prompts.{name}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var templateText = reader.ReadToEnd();

        return Parser.Parse(templateText)
            ?? throw new InvalidOperationException($"Failed to parse prompt template: {name}");
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
        var markerTimestamp = ReadMarkerTimestamp();
        var contextCutoff = markerTimestamp - ContextWindow;

        var allFiles = Directory.GetFiles(historyDir, "*.jsonl")
            .OrderBy(f => f)
            .ToList();

        if (allFiles.Count == 0)
            return;

        var contextEntries = new List<string>();
        var newEntries = new List<string>();
        DateTime latestTimestamp = markerTimestamp;

        foreach (var file in allFiles)
        {
            if (File.GetLastWriteTimeUtc(file) < contextCutoff)
                continue;

            try
            {
                var lines = await File.ReadAllLinesAsync(file, ct);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<TranscriptionHistoryEntry>(line, CamelCase);
                        if (entry is null || string.IsNullOrWhiteSpace(entry.RawText))
                            continue;

                        if (entry.Timestamp > markerTimestamp)
                        {
                            newEntries.Add(entry.RawText);
                            if (entry.Timestamp > latestTimestamp)
                                latestTimestamp = entry.Timestamp;
                        }
                        else if (entry.Timestamp >= contextCutoff)
                        {
                            contextEntries.Add(entry.RawText);
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HotwordLearning] Failed to read: {File}", file);
            }
        }

        if (newEntries.Count == 0)
            return;

        var entriesToAnalyze = contextEntries.Concat(newEntries)
            .TakeLast(MaxEntriesPerSession)
            .ToList();

        _logger.LogInformation("[HotwordLearning] Analyzing {Total} transcripts ({New} new + {Context} context).",
            entriesToAnalyze.Count, newEntries.Count, contextEntries.Count);

        var existing = await LoadExistingHotwordsAsync(ct);
        var discoveredWords = await ExtractHotwordsFromSessionAsync(entriesToAnalyze, ct);

        if (discoveredWords.Count > 0)
        {
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var newWords = discoveredWords.Where(w => !existingSet.Contains(w)).ToList();

            if (newWords.Count > 0)
            {
                var merged = existing.Concat(newWords).ToList();
                await WriteHotwordsAtomicAsync(merged, ct);
                _logger.LogInformation("[HotwordLearning] Added {Count} new hotwords: {Words}",
                    newWords.Count, string.Join(", ", newWords));
            }
        }

        WriteMarkerTimestamp(latestTimestamp);
    }

    private async Task<List<string>> ExtractHotwordsFromSessionAsync(
        List<string> transcripts, CancellationToken ct)
    {
        var numberedTranscripts = string.Join("\n", transcripts.Select((t, i) => $"[{i + 1}] {t}"));

        var systemPrompt = await _promptTemplate.RenderAsync(new TemplateContext());

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

    private void WriteMarkerTimestamp(DateTime timestamp)
    {
        try
        {
            File.WriteAllText(_markerFilePath, timestamp.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HotwordLearning] Failed to write marker file.");
        }
    }
}
