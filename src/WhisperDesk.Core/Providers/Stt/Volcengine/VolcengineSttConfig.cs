namespace WhisperDesk.Core.Providers.Stt.Volcengine;

/// <summary>
/// Configuration for Volcengine Doubao bigmodel ASR provider.
/// Bound from "VolcengineSpeech" section in appsettings.json.
/// Supports two auth modes:
///   1. API Key auth (preferred): set ApiKey only.
///   2. Token auth (legacy):      set AppKey + AccessKey.
/// </summary>
public class VolcengineSttConfig
{
    /// <summary>API Key for Volcengine Doubao ASR (x-api-key header). Preferred auth method.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Application ID (X-Api-App-Key header). Used only when ApiKey is empty.</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>Access token (X-Api-Access-Key header). Used only when ApiKey is empty.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Service resource identifier (X-Api-Resource-Id header).</summary>
    public string ResourceId { get; set; } = "volc.seedasr.sauc.duration";

    /// <summary>True when at least one auth method is configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) ||
        (!string.IsNullOrWhiteSpace(AppKey) && !string.IsNullOrWhiteSpace(AccessKey));

    /// <summary>True when using the API Key auth mode.</summary>
    public bool UseApiKeyAuth => !string.IsNullOrWhiteSpace(ApiKey);
}
