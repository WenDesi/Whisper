namespace WhisperDesk.Core.Contract;

/// <summary>
/// UI-facing contract for controlling the transcription pipeline.
/// The WPF layer depends only on this interface — never on concrete pipeline types.
/// </summary>
public interface IPipelineController : IDisposable
{
    /// <summary>Start a new recording/transcription session.</summary>
    Task StartSessionAsync(WindowTextSerializationInfo? textContext = null, SessionMode mode = SessionMode.Transcribe, CancellationToken ct = default);

    /// <summary>Stop recording and process the captured audio through the pipeline.</summary>
    /// <param name="modeOverride">If supplied, overrides the mode that was set at start time (used for hotkey-modifier dynamic switching).</param>
    Task<PipelineResult?> StopSessionAsync(SessionMode? modeOverride = null, CancellationToken ct = default);

    /// <summary>Abort the current session immediately, discarding partial results.</summary>
    Task AbortSessionAsync();

    /// <summary>Current pipeline state.</summary>
    PipelineState State { get; }

    /// <summary>Most recent cleaned text (for paste hotkey).</summary>
    string? LastProcessedText { get; }

    /// <summary>True if recording data is available for export.</summary>
    bool HasRecordingData { get; }

    /// <summary>Get the current recording as a WAV byte array, or null if unavailable.</summary>
    byte[]? GetRecordingAsWav();

    /// <summary>Fired when the pipeline state changes.</summary>
    event EventHandler<PipelineState> StateChanged;

    /// <summary>Fired with partial transcript text during recognition.</summary>
    event EventHandler<string> PartialTranscriptUpdated;

    /// <summary>Fired with each incremental chunk of cleaned text during streaming post-processing (Transcribe mode).</summary>
    event EventHandler<string> CleanupChunkProduced;

    /// <summary>Fired when the pipeline completes with a result.</summary>
    event EventHandler<PipelineResult> SessionCompleted;

    /// <summary>Fired when a pipeline error occurs.</summary>
    event EventHandler<PipelineError> ErrorOccurred;

    /// <summary>Fired when the pipeline issues a local command (e.g. append/replace) that the UI must execute.</summary>
    event EventHandler<CommandEvent> LocalCommandExecuted;

    /// <summary>Deliver the UI's execution result back to the pipeline so it can unblock the waiting LLM tool call.</summary>
    void SendCommandResult(CommandResult commandResult);
}
