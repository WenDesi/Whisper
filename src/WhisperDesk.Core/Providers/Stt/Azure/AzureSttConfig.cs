namespace WhisperDesk.Core.Providers.Stt.Azure;

/// <summary>
/// Configuration for Azure Speech STT provider.
/// Bound from "AzureSpeech" section in appsettings.json.
/// </summary>
public class AzureSttConfig
{
    public string SubscriptionKey { get; set; } = string.Empty;
    public string Region { get; set; } = "japaneast";
    public string Endpoint { get; set; } = string.Empty;
}
