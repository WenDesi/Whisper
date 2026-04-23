namespace WhisperDesk.Core.Models;

/// <summary>
/// Pipeline lifecycle states.
/// </summary>
public enum PipelineState
{
    /// <summary>No active session. Ready to start.</summary>
    Idle,

    /// <summary>Microphone capture active, streaming to STT.</summary>
    Listening,

    /// <summary>Audio complete, waiting for final STT results.</summary>
    Transcribing,

    /// <summary>Running post-processing stages (LLM cleanup, etc.).</summary>
    PostProcessing,

    /// <summary>Pipeline complete, result available.</summary>
    Completed,

    /// <summary>Pipeline encountered an error.</summary>
    Error
}
