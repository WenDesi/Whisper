namespace WhisperDesk.Models;

public class WhisperDeskSettings
{
    public AzureOpenAISettings AzureOpenAI { get; set; } = new();
    public AzureSpeechSettings AzureSpeech { get; set; } = new();
    public VolcengineSpeechSettings VolcengineSpeech { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
}

public class AzureOpenAISettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = "gpt-5-mini";
}

public class AzureSpeechSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string SubscriptionKey { get; set; } = string.Empty;
    public string Region { get; set; } = "japaneast";
    public string Language { get; set; } = "zh-CN";
}

public class HotkeySettings
{
    public string PushToTalk { get; set; } = "F9";
    public string PasteTranscription { get; set; } = "Ctrl+Shift+V";
}

public class AudioSettings
{
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
}

public class TranscriptionSettings
{
    /// <summary>
    /// Speech-to-text provider: "AzureSpeech"
    /// </summary>
    public string SpeechProvider { get; set; } = "AzureSpeech";

    /// <summary>
    /// Text cleanup provider: "AzureOpenAI" (uses GPT for filler removal)
    /// </summary>
    public string CleanupProvider { get; set; } = "AzureOpenAI";

    public string Language { get; set; } = "zh";
    public string Prompt { get; set; } = "This audio contains Chinese speech with occasional English technical terms like Redis, Kubernetes, Docker, API, SDK, Azure, etc.";
    public string LogFile { get; set; } = "transcription-history.log";
}

public class RecordingSettings
{
    /// <summary>
    /// Directory path where recordings are saved. If empty, the save recording feature is disabled.
    /// </summary>
    public string SavePath { get; set; } = string.Empty;
}

public class VolcengineSpeechSettings
{
    /// <summary>API Key for Volcengine Doubao ASR.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Service resource identifier (e.g., "volc.seedasr.sauc.duration").</summary>
    public string ResourceId { get; set; } = "volc.seedasr.sauc.duration";
}
