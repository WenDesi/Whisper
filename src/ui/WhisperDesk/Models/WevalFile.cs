using System.IO;
using System.Text;

namespace WhisperDesk.Models;

/// <summary>
/// Represents a WhisperDesk evaluation file (.weval) — a custom binary format
/// containing timestamped audio + corrected transcription for quality evaluation.
///
/// Binary format:
///   [4 bytes]  Magic: "WEVL" (ASCII)
///   [4 bytes]  Version: uint32 = 1
///   [8 bytes]  Timestamp: int64 (UTC ticks)
///   [4 bytes]  CorrectedTextLength: uint32 (byte length of UTF-8 encoded text)
///   [N bytes]  CorrectedText: UTF-8 encoded string
///   [4 bytes]  AudioLength: uint32 (byte length of WAV data)
///   [N bytes]  AudioData: complete WAV file bytes
/// </summary>
public class WevalFile
{
    private static readonly byte[] Magic = "WEVL"u8.ToArray();
    private const uint CurrentVersion = 1;

    public DateTime Timestamp { get; set; }
    public string CorrectedText { get; set; } = string.Empty;
    public byte[] AudioData { get; set; } = [];

    /// <summary>
    /// Save this evaluation to a .weval binary file.
    /// </summary>
    public void Save(string filePath)
    {
        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        // Magic + version
        writer.Write(Magic);
        writer.Write(CurrentVersion);

        // Timestamp as UTC ticks
        writer.Write(Timestamp.ToUniversalTime().Ticks);

        // Corrected text
        var correctedBytes = Encoding.UTF8.GetBytes(CorrectedText);
        writer.Write((uint)correctedBytes.Length);
        writer.Write(correctedBytes);

        // Audio data
        writer.Write((uint)AudioData.Length);
        writer.Write(AudioData);
    }

    /// <summary>
    /// Load an evaluation from a .weval binary file.
    /// </summary>
    public static WevalFile Load(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        // Validate magic
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] ||
            magic[2] != Magic[2] || magic[3] != Magic[3])
        {
            throw new InvalidDataException("Not a valid .weval file (bad magic bytes).");
        }

        // Version check
        var version = reader.ReadUInt32();
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported .weval version {version} (expected {CurrentVersion}).");
        }

        var weval = new WevalFile();

        // Timestamp
        var ticks = reader.ReadInt64();
        weval.Timestamp = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();

        // Corrected text
        var correctedLen = reader.ReadUInt32();
        var correctedBytes = reader.ReadBytes((int)correctedLen);
        weval.CorrectedText = Encoding.UTF8.GetString(correctedBytes);

        // Audio data
        var audioLen = reader.ReadUInt32();
        weval.AudioData = reader.ReadBytes((int)audioLen);

        return weval;
    }
}
