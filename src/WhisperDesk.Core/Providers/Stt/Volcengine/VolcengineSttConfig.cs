namespace WhisperDesk.Core.Providers.Stt.Volcengine;

/// <summary>
/// Configuration for Volcengine Doubao bigmodel ASR provider.
/// Bound from "VolcengineSpeech" section in appsettings.json.
/// </summary>
public class VolcengineSttConfig
{
    /// <summary>API Key for Volcengine Doubao ASR (x-api-key header).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Service resource identifier (X-Api-Resource-Id header).</summary>
    public string ResourceId { get; set; } = "volc.seedasr.sauc.duration";
}
