namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// Contributes context (phrase hints, language preferences, etc.) to an STT session.
/// Runs in parallel with audio capture startup — must not block the audio path.
/// </summary>
public interface IContextProvider
{
    /// <summary>Display name for logging/diagnostics.</summary>
    string Name { get; }

    /// <summary>Execution order. Lower values run first.</summary>
    int Order { get; }

    /// <summary>
    /// Contribute context to the session builder. Called once per session during startup.
    /// Providers run sequentially in Order.
    /// </summary>
    Task ContributeAsync(SessionContextBuilder builder, CancellationToken ct = default);
}
