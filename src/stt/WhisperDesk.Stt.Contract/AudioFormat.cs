namespace WhisperDesk.Stt.Contract;

public record AudioFormat
{
    public int SampleRate { get; init; } = 16000;
    public int BitsPerSample { get; init; } = 16;
    public int Channels { get; init; } = 1;

    public int ByteRate => SampleRate * Channels * (BitsPerSample / 8);
    public int BlockAlign => Channels * (BitsPerSample / 8);

    public static AudioFormat Default => new();
}
