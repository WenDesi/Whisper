namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// Optional synchronous audio transform applied to audio chunks before STT.
/// Implementations MUST be fast (&lt;1ms per chunk). Not used in V1 but defined for extensibility.
/// </summary>
public interface IAudioInterceptor
{
    /// <summary>Display name for logging/diagnostics.</summary>
    string Name { get; }

    /// <summary>Execution order (lower = earlier).</summary>
    int Order { get; }

    /// <summary>
    /// Process an audio chunk in-place. Must be synchronous and fast.
    /// Return the (possibly transformed) audio data.
    /// </summary>
    ReadOnlyMemory<byte> Process(ReadOnlyMemory<byte> audioChunk);
}
