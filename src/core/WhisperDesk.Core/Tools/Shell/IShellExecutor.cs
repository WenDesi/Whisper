namespace WhisperDesk.Core.Tools.Shell;

public enum ShellKind
{
    PowerShell,
    Cmd
}

public class ShellExecutionRequest
{
    public required string Command { get; init; }
    public ShellKind Shell { get; init; } = ShellKind.PowerShell;
    public string? WorkingDirectory { get; init; }
    public int TimeoutMs { get; init; } = 30_000;
}

public class ShellExecutionResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public required bool TimedOut { get; init; }
}

/// <summary>
/// Executes shell commands on the local Windows machine. Implementations are
/// platform-specific; see <see cref="WindowsShellExecutor"/>.
/// </summary>
public interface IShellExecutor
{
    Task<ShellExecutionResult> ExecuteAsync(ShellExecutionRequest request, CancellationToken ct);
}
