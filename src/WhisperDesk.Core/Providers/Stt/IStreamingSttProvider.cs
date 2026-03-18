using WhisperDesk.Core.Models;

namespace WhisperDesk.Core.Providers.Stt;

/// <summary>
/// Pluggable streaming speech-to-text provider.
/// Audio is pushed via PushAudio; results arrive via events.
/// </summary>
public interface IStreamingSttProvider : IDisposable
{
    /// <summary>Provider display name (e.g., "Azure Speech", "Volcengine").</summary>
    string Name { get; }

    /// <summary>Start a new STT session with the given options.</summary>
    Task StartSessionAsync(SttSessionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Push an audio chunk to the provider. Non-blocking, hot-path.
    /// Called from the audio capture thread — must return fast.
    /// </summary>
    void PushAudio(ReadOnlyMemory<byte> audioData);

    /// <summary>Signal that no more audio will be sent. Provider should finalize remaining results.</summary>
    void SignalEndOfAudio();

    /// <summary>End the STT session and return the final concatenated transcript.</summary>
    Task<string> EndSessionAsync();

    /// <summary>Fired for partial (interim) recognition results.</summary>
    event EventHandler<SttPartialResult> PartialResultReceived;

    /// <summary>Fired for finalized recognition results (sentence/segment complete).</summary>
    event EventHandler<SttFinalResult> FinalResultReceived;

    /// <summary>Fired when the provider encounters an error.</summary>
    event EventHandler<SttError> ErrorOccurred;
}
