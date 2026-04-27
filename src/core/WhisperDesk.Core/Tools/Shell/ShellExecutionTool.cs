using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Core.Tools.Shell;

/// <summary>
/// LLM-facing tool that runs a shell command on the local Windows machine
/// (PowerShell or cmd). Returns a JSON blob containing exit code, stdout, and stderr
/// so the model can reason about the result.
/// </summary>
public class ShellExecutionTool : ILocalTool
{
    private readonly ILogger<ShellExecutionTool> _logger;
    private readonly IShellExecutor _executor;

    public string Name => "shell_exec";

    public string Description =>
        "Execute a shell command on the local Windows machine and return its output. " +
        "Use this when the user's instruction requires running a command (e.g. listing files, " +
        "running a script, querying system state). Choose 'powershell' for PowerShell syntax, " +
        "'cmd' for batch/legacy commands. Returns a JSON object with exitCode, stdout, stderr, and timedOut.";

    public string ParametersSchema => """
        {
          "type":"object",
          "properties":{
            "command":{"type":"string","description":"The command line to execute."},
            "shell":{"type":"string","enum":["powershell","cmd"],"description":"Which shell to use. Defaults to powershell."},
            "workingDirectory":{"type":"string","description":"Optional working directory. Defaults to the app's CWD."},
            "timeoutMs":{"type":"integer","description":"Timeout in milliseconds. Defaults to 30000."}
          },
          "required":["command"]
        }
        """;

    public ShellExecutionTool(ILogger<ShellExecutionTool> logger, IShellExecutor executor)
    {
        _logger = logger;
        _executor = executor;
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        var command = root.GetProperty("command").GetString()
            ?? throw new ArgumentException("'command' is required.");

        var shell = ShellKind.PowerShell;
        if (root.TryGetProperty("shell", out var shellEl) && shellEl.ValueKind == JsonValueKind.String)
        {
            shell = shellEl.GetString()?.ToLowerInvariant() switch
            {
                "cmd" => ShellKind.Cmd,
                "powershell" or "pwsh" or null or "" => ShellKind.PowerShell,
                var other => throw new ArgumentException($"Unsupported shell '{other}'.")
            };
        }

        string? workingDir = null;
        if (root.TryGetProperty("workingDirectory", out var wdEl) && wdEl.ValueKind == JsonValueKind.String)
        {
            workingDir = wdEl.GetString();
        }

        var timeoutMs = 30_000;
        if (root.TryGetProperty("timeoutMs", out var toEl) && toEl.ValueKind == JsonValueKind.Number)
        {
            timeoutMs = toEl.GetInt32();
        }

        var result = await _executor.ExecuteAsync(new ShellExecutionRequest
        {
            Command = command,
            Shell = shell,
            WorkingDirectory = workingDir,
            TimeoutMs = timeoutMs
        }, ct);

        return JsonSerializer.Serialize(new
        {
            exitCode = result.ExitCode,
            stdout = result.StandardOutput,
            stderr = result.StandardError,
            timedOut = result.TimedOut
        });
    }
}
