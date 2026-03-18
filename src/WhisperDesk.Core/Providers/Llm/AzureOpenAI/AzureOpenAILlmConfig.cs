namespace WhisperDesk.Core.Providers.Llm.AzureOpenAI;

/// <summary>
/// Configuration for Azure OpenAI LLM provider.
/// Bound from "AzureOpenAI" section in appsettings.json.
/// </summary>
public class AzureOpenAILlmConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = "gpt-5-mini";
}
