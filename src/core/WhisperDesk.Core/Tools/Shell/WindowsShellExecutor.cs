using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Core.Tools.Shell;

/// <summary>
/// Executes commands on Windows by spawning powershell.exe or cmd.exe directly.
/// Output is captured from stdout/stderr; the process is killed on timeout.
/// </summary>
public class WindowsShellExecutor : IShellExecutor
{
    private readonly ILogger<WindowsShellExecutor> _logger;

    public WindowsShellExecutor(ILogger<WindowsShellExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<ShellExecutionResult> ExecuteAsync(ShellExecutionRequest request, CancellationToken ct)
    {
        var (fileName, arguments) = BuildInvocation(request.Shell, request.Command);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        _logger.LogInformation("[Shell] Executing {Shell}: {Command}", request.Shell, request.Command);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(request.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception killEx) { _logger.LogWarning(killEx, "[Shell] Failed to kill process after cancel."); }
        }

        // Drain async readers
        process.WaitForExit();

        var result = new ShellExecutionResult
        {
            ExitCode = process.HasExited ? process.ExitCode : -1,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            TimedOut = timedOut
        };

        _logger.LogInformation("[Shell] Exit={ExitCode} TimedOut={TimedOut} StdoutLen={OutLen} StderrLen={ErrLen}",
            result.ExitCode, result.TimedOut, result.StandardOutput.Length, result.StandardError.Length);

        return result;
    }

    private static (string fileName, string arguments) BuildInvocation(ShellKind shell, string command)
    {
        return shell switch
        {
            ShellKind.PowerShell => (
                ResolvePowerShell(),
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Escape(command)}\""
            ),
            ShellKind.Cmd => (
                "cmd.exe",
                $"/d /c \"{command}\""
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, null)
        };
    }

    private static string ResolvePowerShell()
    {
        // Prefer PowerShell 7+ if available; fall back to Windows PowerShell.
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
        {
            if (ExistsOnPath(candidate)) return candidate;
        }
        return "powershell.exe";
    }

    private static bool ExistsOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir.Trim(), fileName))) return true;
            }
            catch { /* ignore malformed PATH entries */ }
        }
        return false;
    }

    private static string Escape(string command) =>
        command.Replace("\"", "\\\"");
}
