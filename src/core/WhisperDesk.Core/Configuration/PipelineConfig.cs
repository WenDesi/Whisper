namespace WhisperDesk.Core.Configuration;

/// <summary>
/// Pipeline configuration. Read from "Pipeline" section in appsettings.json.
/// </summary>
public class PipelineConfig
{
    /// <summary>STT provider key (e.g., "AzureSpeech", "Volcengine").</summary>
    public string SttProvider { get; set; } = "AzureSpeech";

    /// <summary>LLM provider key (e.g., "AzureOpenAI").</summary>
    public string LlmProvider { get; set; } = "AzureOpenAI";

    /// <summary>Default language code.</summary>
    public string Language { get; set; } = "zh";

    /// <summary>Languages for auto-detection.</summary>
    public List<string> AutoDetectLanguages { get; set; } = ["zh-CN", "en-US"];

    /// <summary>Path to hot words JSON file (relative to exe directory).</summary>
    public string HotWordsFile { get; set; } = "hotwords.json";

    /// <summary>Enable LLM text cleanup post-processing.</summary>
    public bool EnableTextCleanup { get; set; } = true;

    /// <summary>WASAPI device ID for microphone selection. Empty string = system default.</summary>
    public string AudioDeviceId { get; set; } = "";

    /// <summary>Audio format settings.</summary>
    public AudioFormatConfig Audio { get; set; } = new();

    public double HistorySessionGapMinutes { get; set; } = 10;
}

public class AudioFormatConfig
{
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
}
