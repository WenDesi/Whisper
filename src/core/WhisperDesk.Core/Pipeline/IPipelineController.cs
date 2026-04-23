using WhisperDesk.Core.Models;

namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// UI-facing contract for controlling the transcription pipeline.
/// The WPF layer depends only on this interface — never on concrete pipeline types.
/// </summary>
public interface IPipelineController : IDisposable
{
    /// <summary>Start a new recording/transcription session.</summary>
    Task StartSessionAsync(string foregroundProcess = "", string foregroundWindowTitle = "", CancellationToken ct = default);

    /// <summary>Stop recording and process the captured audio through the pipeline.</summary>
    Task<PipelineResult?> StopSessionAsync(CancellationToken ct = default);

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

    /// <summary>Fired when the pipeline completes with a result.</summary>
    event EventHandler<PipelineResult> SessionCompleted;

    /// <summary>Fired when a pipeline error occurs.</summary>
    event EventHandler<PipelineError> ErrorOccurred;
}
