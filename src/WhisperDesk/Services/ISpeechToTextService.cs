namespace WhisperDesk.Services;

/// <summary>
/// Speech-to-text provider interface. Implementations can be swapped via config.
/// </summary>
public interface ISpeechToTextService
{
    /// <summary>
    /// Transcribe audio data (WAV format, 16kHz, 16-bit, mono) to text.
    /// </summary>
    Task<string> TranscribeAsync(byte[] audioData, string? language = null, CancellationToken ct = default);
}
