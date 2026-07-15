namespace WhisperDesk.Stt.Provider.Volcengine;

public class VolcengineSttConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string Mode { get; set; } = "AsyncFinal";
    public string ResourceId { get; set; } = "volc.seedasr.sauc.duration";
    public string Endpoint { get; set; } = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async";
    public bool EnableNonstream { get; set; } = true;
    public string ResultType { get; set; } = "single";
    public bool ShowUtterances { get; set; } = true;
    public int? EndWindowSizeMs { get; set; } = 500;
    public int? ForceToSpeechTimeMs { get; set; } = 1000;
    public int FinalizationTimeoutMs { get; set; } = 4000;
}
