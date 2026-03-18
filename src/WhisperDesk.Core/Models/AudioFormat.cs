namespace WhisperDesk.Core.Models;

/// <summary>
/// Describes the PCM audio format used throughout the pipeline.
/// </summary>
public record AudioFormat
{
    public int SampleRate { get; init; } = 16000;
    public int BitsPerSample { get; init; } = 16;
    public int Channels { get; init; } = 1;

    /// <summary>Bytes per second = SampleRate * Channels * (BitsPerSample / 8).</summary>
    public int ByteRate => SampleRate * Channels * (BitsPerSample / 8);

    /// <summary>Block alignment = Channels * (BitsPerSample / 8).</summary>
    public int BlockAlign => Channels * (BitsPerSample / 8);

    /// <summary>Default format: 16kHz, 16-bit, mono PCM.</summary>
    public static AudioFormat Default => new();
}
