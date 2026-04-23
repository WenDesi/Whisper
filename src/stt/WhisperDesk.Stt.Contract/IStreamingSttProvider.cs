namespace WhisperDesk.Stt.Contract;

public interface IStreamingSttProvider : IDisposable
{
    string Name { get; }
    Task StartSessionAsync(SttSessionOptions options, CancellationToken ct = default);
    void PushAudio(ReadOnlyMemory<byte> audioData);
    void SignalEndOfAudio();
    Task<string> EndSessionAsync();
    event EventHandler<SttPartialResult> PartialResultReceived;
    event EventHandler<SttFinalResult> FinalResultReceived;
    event EventHandler<SttError> ErrorOccurred;
}
