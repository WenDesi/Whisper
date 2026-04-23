using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Pipeline;

namespace WhisperDesk.Core.Stages.PreProcessing;

/// <summary>
/// Loads hot words from a JSON file and contributes them as phrase hints to the STT session.
/// File format: { "hotwords": ["Redis", "Kubernetes", "Docker", ...] }
/// </summary>
public class HotWordContextProvider : IContextProvider
{
    private readonly ILogger<HotWordContextProvider> _logger;
    private readonly string _hotWordsFilePath;

    private List<string> _cachedWords = [];
    private DateTime _cachedLastWriteUtc;

    public string Name => "Hot Words";

    public HotWordContextProvider(ILogger<HotWordContextProvider> logger, PipelineConfig config)
    {
        _logger = logger;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk");
        Directory.CreateDirectory(appDataDir);
        _hotWordsFilePath = Path.Combine(appDataDir, config.HotWordsFile);
    }

    public async Task ContributeAsync(SessionContextBuilder builder, CancellationToken ct = default)
    {
        if (!File.Exists(_hotWordsFilePath))
        {
            _logger.LogDebug("[HotWords] File not found: {Path}. Skipping.", _hotWordsFilePath);
            return;
        }

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(_hotWordsFilePath);

            if (_cachedWords.Count > 0 && lastWrite == _cachedLastWriteUtc)
            {
                builder.AddPhraseHints(_cachedWords);
                _logger.LogDebug("[HotWords] Using cached {Count} hot words.", _cachedWords.Count);
                return;
            }

            var json = await File.ReadAllTextAsync(_hotWordsFilePath, ct);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("hotwords", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var words = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                _cachedWords = words;
                _cachedLastWriteUtc = lastWrite;

                builder.AddPhraseHints(words);
                _logger.LogInformation("[HotWords] Loaded {Count} hot words from {Path}.", words.Count, _hotWordsFilePath);
            }
            else
            {
                _logger.LogWarning("[HotWords] No 'hotwords' array found in {Path}.", _hotWordsFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HotWords] Failed to load hot words from {Path}.", _hotWordsFilePath);
        }
    }
}
