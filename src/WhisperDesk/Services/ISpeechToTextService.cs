namespace WhisperDesk.Services;

/// <summary>
/// Speech-to-text provider interface. Uses microphone directly.
/// </summary>
public interface ISpeechToTextService
{
    /// <summary>
    /// Start listening from the default microphone.
    /// </summary>
    Task StartListeningAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop listening and return the transcribed text.
    /// </summary>
    Task<string> StopListeningAsync();
}
