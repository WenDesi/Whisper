namespace WhisperDesk.Llm.Provider.AzureOpenAI;

public class AzureOpenAILlmConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = "gpt-5-mini";
}
