namespace WhisperDesk.Core.Contract;

public interface IPipelineController
{
    Task StartSessionAsync(CancellationToken ct = default);
    Task<PipelineResult?> StopSessionAsync(CancellationToken ct = default);
    Task AbortSessionAsync();
    PipelineState State { get; }
    string? LastProcessedText { get; }
    bool HasRecordingData { get; }
    byte[]? GetRecordingAsWav();
    event EventHandler<PipelineState> StateChanged;
    event EventHandler<string> PartialTranscriptUpdated;
    event EventHandler<PipelineResult> SessionCompleted;
    event EventHandler<PipelineError> ErrorOccurred;
}
