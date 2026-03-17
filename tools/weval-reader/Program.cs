using System.Text;

if (args.Length == 0)
{
    Console.WriteLine("WhisperDesk .weval File Reader");
    Console.WriteLine("==============================");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  weval-reader <file.weval>              Show metadata and text");
    Console.WriteLine("  weval-reader <file.weval> --export-wav Export audio to .wav file");
    Console.WriteLine("  weval-reader <dir>                     List all .weval files in directory");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  weval-reader recording.weval");
    Console.WriteLine("  weval-reader recording.weval --export-wav");
    Console.WriteLine("  weval-reader D:\\recordings\\");
    return 1;
}

var path = args[0];
var exportWav = args.Contains("--export-wav", StringComparer.OrdinalIgnoreCase);

// Directory mode: list all .weval files
if (Directory.Exists(path))
{
    var files = Directory.GetFiles(path, "*.weval", SearchOption.TopDirectoryOnly)
        .OrderBy(f => f)
        .ToArray();

    if (files.Length == 0)
    {
        Console.WriteLine($"No .weval files found in: {path}");
        return 1;
    }

    Console.WriteLine($"Found {files.Length} .weval file(s) in: {path}");
    Console.WriteLine(new string('-', 80));
    Console.WriteLine($"{"File",-40} {"Timestamp",-22} {"Audio",-10} {"Text Preview"}");
    Console.WriteLine(new string('-', 80));

    foreach (var file in files)
    {
        try
        {
            var weval = WevalFile.Load(file);
            var name = Path.GetFileName(file);
            var audioDuration = GetAudioDuration(weval.AudioData);
            var preview = Truncate(weval.CorrectedText, 40);
            Console.WriteLine($"{name,-40} {weval.Timestamp,-22:yyyy-MM-dd HH:mm:ss} {audioDuration,-10} {preview}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(file),-40} ERROR: {ex.Message}");
        }
    }
    return 0;
}

// Single file mode
if (!File.Exists(path))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"File not found: {path}");
    Console.ResetColor();
    return 1;
}

try
{
    var weval = WevalFile.Load(path);

    Console.WriteLine("WhisperDesk Evaluation File (.weval)");
    Console.WriteLine(new string('=', 50));
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("Timestamp:      ");
    Console.ResetColor();
    Console.WriteLine(weval.Timestamp.ToString("yyyy-MM-dd HH:mm:ss (zzz)"));

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("Audio:          ");
    Console.ResetColor();
    Console.WriteLine($"{GetAudioDuration(weval.AudioData)} ({weval.AudioData.Length:N0} bytes)");

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("File size:      ");
    Console.ResetColor();
    Console.WriteLine($"{new FileInfo(path).Length:N0} bytes");

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("--- Corrected Text ---");
    Console.ResetColor();
    Console.WriteLine(string.IsNullOrEmpty(weval.CorrectedText) ? "(empty)" : weval.CorrectedText);

    // Export WAV if requested
    if (exportWav)
    {
        var wavPath = Path.ChangeExtension(path, ".wav");
        File.WriteAllBytes(wavPath, weval.AudioData);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Audio exported to: {wavPath}");
        Console.ResetColor();
    }

    return 0;
}
catch (InvalidDataException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Invalid .weval file: {ex.Message}");
    Console.ResetColor();
    return 1;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Error reading file: {ex.Message}");
    Console.ResetColor();
    return 1;
}

// --- Helper functions ---

static string GetAudioDuration(byte[] wavData)
{
    if (wavData.Length < 44) return "N/A";

    var sampleRate = BitConverter.ToUInt32(wavData, 24);
    var bitsPerSample = BitConverter.ToUInt16(wavData, 34);
    var channels = BitConverter.ToUInt16(wavData, 22);
    var dataSize = BitConverter.ToUInt32(wavData, 40);

    if (sampleRate == 0 || bitsPerSample == 0 || channels == 0) return "N/A";

    var bytesPerSample = bitsPerSample / 8.0 * channels;
    var totalSamples = dataSize / bytesPerSample;
    var duration = TimeSpan.FromSeconds(totalSamples / sampleRate);

    return duration.TotalMinutes >= 1
        ? $"{duration:mm\\:ss}"
        : $"{duration.TotalSeconds:F1}s";
}

static string Truncate(string text, int maxLen)
{
    var oneLine = text.ReplaceLineEndings(" ").Trim();
    return oneLine.Length <= maxLen ? oneLine : oneLine[..(maxLen - 3)] + "...";
}

// --- WevalFile (self-contained copy for the tool) ---

class WevalFile
{
    private static readonly byte[] Magic = "WEVL"u8.ToArray();
    private const uint CurrentVersion = 1;

    public DateTime Timestamp { get; set; }
    public string CorrectedText { get; set; } = string.Empty;
    public byte[] AudioData { get; set; } = [];

    public static WevalFile Load(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] ||
            magic[2] != Magic[2] || magic[3] != Magic[3])
        {
            throw new InvalidDataException("Not a valid .weval file (bad magic bytes).");
        }

        var version = reader.ReadUInt32();
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported .weval version {version} (expected {CurrentVersion}).");
        }

        var weval = new WevalFile();

        var ticks = reader.ReadInt64();
        weval.Timestamp = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();

        var correctedLen = reader.ReadUInt32();
        var correctedBytes = reader.ReadBytes((int)correctedLen);
        weval.CorrectedText = Encoding.UTF8.GetString(correctedBytes);

        var audioLen = reader.ReadUInt32();
        weval.AudioData = reader.ReadBytes((int)audioLen);

        return weval;
    }
}
