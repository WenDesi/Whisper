namespace WhisperDesk.Llm.Provider.OpenAI;

public class OpenAILlmConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o";
}
