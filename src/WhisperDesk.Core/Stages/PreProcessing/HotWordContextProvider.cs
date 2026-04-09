using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Diagnostics;
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

    public string Name => "Hot Words";

    public HotWordContextProvider(ILogger<HotWordContextProvider> logger, PipelineConfig config)
    {
        _logger = logger;

        // Resolve hot words file relative to exe directory
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;
        _hotWordsFilePath = Path.Combine(exeDir, config.HotWordsFile);
    }

    public async Task ContributeAsync(SessionContextBuilder builder, CancellationToken ct = default)
    {
        using var activity = DiagnosticSources.Pipeline.StartActivity("HotWordContext.Contribute");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("hotwords.file", _hotWordsFilePath);

        if (!File.Exists(_hotWordsFilePath))
        {
            _logger.LogDebug("[HotWords] File not found: {Path}. Skipping.", _hotWordsFilePath);
            activity?.SetTag("result", "file_not_found");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_hotWordsFilePath, ct);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("hotwords", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var words = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                builder.AddPhraseHints(words);
                _logger.LogInformation("[HotWords] Loaded {Count} hot words from {Path}.", words.Count, _hotWordsFilePath);
                activity?.SetTag("hotwords.count", words.Count);
            }
            else
            {
                _logger.LogWarning("[HotWords] No 'hotwords' array found in {Path}.", _hotWordsFilePath);
                activity?.SetTag("result", "no_array_found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HotWords] Failed to load hot words from {Path}.", _hotWordsFilePath);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
        }
    }
}
