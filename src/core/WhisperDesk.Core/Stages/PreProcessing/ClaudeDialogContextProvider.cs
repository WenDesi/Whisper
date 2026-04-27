using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Pipeline;

namespace WhisperDesk.Core.Stages.PreProcessing;

/// <summary>
/// Reads recent Claude Code conversation turns from the local JSONL files
/// and contributes them as dialog context for STT recognition improvement.
/// </summary>
public class ClaudeDialogContextProvider : IContextProvider
{
    private readonly ILogger<ClaudeDialogContextProvider> _logger;
    private readonly int _maxTurns;

    public string Name => "Claude Dialog Context";
    public int Order => 10;

    public ClaudeDialogContextProvider(ILogger<ClaudeDialogContextProvider> logger, PipelineConfig config)
    {
        _logger = logger;
        _maxTurns = config.DialogContextMaxTurns;
        _logger.LogDebug("[ClaudeDialog] Initialized with maxTurns={MaxTurns}", _maxTurns);
    }

    public async Task ContributeAsync(SessionContextBuilder builder, CancellationToken ct = default)
    {
        // 1. Check foreground window to determine if user is in Claude Code
        var process = builder.GetMetadata<string>("foregroundProcess") ?? "";
        var title = builder.GetMetadata<string>("foregroundWindowTitle") ?? "";

        _logger.LogDebug("[ClaudeDialog] Foreground: process={Process}, title={Title}", process, title);

        if (!IsClaudeCode(process, title))
        {
            _logger.LogDebug("[ClaudeDialog] Not Claude Code window, skipping dialog context.");
            return;
        }

        // 2. Locate Claude projects directory
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectsDir = Path.Combine(userProfile, ".claude", "projects");

        if (!Directory.Exists(projectsDir))
        {
            _logger.LogDebug("[ClaudeDialog] Claude projects directory not found: {Dir}", projectsDir);
            return;
        }

        // 3. Find the most recently modified JSONL conversation file
        string[] jsonlFiles;
        try
        {
            jsonlFiles = Directory.GetFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories);
            _logger.LogDebug("[ClaudeDialog] Found {Count} JSONL files in {Dir}", jsonlFiles.Length, projectsDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClaudeDialog] Failed to enumerate Claude project files in {Dir}", projectsDir);
            return;
        }

        if (jsonlFiles.Length == 0)
        {
            _logger.LogDebug("[ClaudeDialog] No JSONL files found in {Dir}", projectsDir);
            return;
        }

        var newestFile = jsonlFiles
            .Select(f => (Path: f, LastWrite: File.GetLastWriteTimeUtc(f)))
            .MaxBy(f => f.LastWrite)
            .Path;

        _logger.LogDebug("[ClaudeDialog] Using conversation file: {File}", newestFile);

        // 4. Read and extract recent dialog turns
        try
        {
            var lines = await File.ReadAllLinesAsync(newestFile, ct);
            _logger.LogDebug("[ClaudeDialog] Read {LineCount} lines from conversation file", lines.Length);

            var turns = ExtractRecentTurns(lines, _maxTurns);

            _logger.LogInformation("[ClaudeDialog] Loaded {Count} dialog turns from {File}",
                turns.Count, Path.GetFileName(newestFile));

            foreach (var (role, text) in turns)
            {
                _logger.LogDebug("[ClaudeDialog] Adding turn: role={Role}, textLength={Length}", role, text.Length);
                builder.AddDialogTurn(role, text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClaudeDialog] Failed to read conversation file: {File}", newestFile);
        }
    }

    private static bool IsClaudeCode(string process, string title)
    {
        return process.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || title.Contains("claude", StringComparison.OrdinalIgnoreCase);
    }

    private List<(string Role, string Text)> ExtractRecentTurns(string[] lines, int maxTurns)
    {
        var turns = new List<(string Role, string Text)>();

        // Read from end to find the most recent turns first
        for (int i = lines.Length - 1; i >= 0 && turns.Count < maxTurns; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Only process user and assistant message types
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                if (type != "user" && type != "assistant") continue;

                // Navigate to message.content array
                if (!root.TryGetProperty("message", out var msg)) continue;
                if (!msg.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind != JsonValueKind.Array) continue;

                // Extract only text blocks (skip tool_use, tool_result, images, etc.)
                var textParts = new List<string>();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() == "text"
                        && block.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            // Truncate long messages to stay within token budget
                            textParts.Add(text.Length > 200 ? text[..200] + "..." : text);
                        }
                    }
                }

                if (textParts.Count > 0)
                {
                    turns.Add((type!, string.Join(" ", textParts)));
                    _logger.LogDebug("[ClaudeDialog] Extracted {Role} turn at line {Line}, {Parts} text parts",
                        type, i, textParts.Count);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug("[ClaudeDialog] Skipping malformed JSONL line at index {Index}: {Error}", i, ex.Message);
            }
        }

        // Reverse so turns are in chronological order
        turns.Reverse();
        _logger.LogDebug("[ClaudeDialog] Extracted {Count} total turns from conversation", turns.Count);
        return turns;
    }
}
