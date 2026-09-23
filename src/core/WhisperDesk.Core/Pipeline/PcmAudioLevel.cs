using System.Buffers.Binary;

namespace WhisperDesk.Core.Pipeline;

public static class PcmAudioLevel
{
    public static float CalculateRms(ReadOnlySpan<byte> pcm, int bitsPerSample)
    {
        if (bitsPerSample is not (8 or 16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "PCM metering supports 8, 16, 24 or 32 bits.");

        var bytesPerSample = bitsPerSample / 8;
        if (pcm.Length % bytesPerSample != 0)
            throw new ArgumentException("PCM data must contain complete samples.", nameof(pcm));
        if (pcm.IsEmpty)
            return 0;

        double sum = 0;
        for (var offset = 0; offset < pcm.Length; offset += bytesPerSample)
        {
            var sample = pcm[offset..];
            var normalized = bitsPerSample switch
            {
                8 => (sample[0] - 128) / 128.0,
                16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
                24 => ((sample[0] << 8 | sample[1] << 16 | sample[2] << 24) >> 8) / 8388608.0,
                32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0,
                _ => throw new InvalidOperationException("Unsupported PCM format.")
            };
            sum += normalized * normalized;
        }

        return (float)Math.Sqrt(sum / (pcm.Length / bytesPerSample));
    }
}
