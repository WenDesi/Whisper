namespace WhisperDesk.Core.Contract;

public enum PipelineState
{
    Idle,
    Listening,
    Transcribing,
    PostProcessing,
    Completed,
    Error
}
