namespace WhisperDesk.Core.Contract;

/// <summary>
/// Determines which post-processing stages run after STT for a session.
/// </summary>
public enum SessionMode
{
    /// <summary>Pure dictation: clean up the transcript only, no LLM instruction interpretation.</summary>
    Transcribe = 0,

    /// <summary>Treat the transcript as an instruction to the LLM (cleanup + command).</summary>
    Instruct = 1,
}
