namespace WhisperDesk.Models;

public class WhisperDeskSettings
{
    public AzureOpenAISettings AzureOpenAI { get; set; } = new();
    public AzureSpeechSettings AzureSpeech { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
}

public class AzureOpenAISettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string WhisperDeployment { get; set; } = "whisper";
    public string ChatDeployment { get; set; } = "gpt-4o";
}

public class AzureSpeechSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string SubscriptionKey { get; set; } = string.Empty;
    public string Language { get; set; } = "zh-CN";
}

public class HotkeySettings
{
    public string PushToTalk { get; set; } = "Ctrl+Shift+R";
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
    /// Speech-to-text provider: "AzureSpeech" or "AzureOpenAI"
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
