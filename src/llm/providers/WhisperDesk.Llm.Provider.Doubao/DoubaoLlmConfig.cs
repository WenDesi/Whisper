namespace WhisperDesk.Llm.Provider.Doubao;

public class DoubaoLlmConfig
{
    public string Endpoint { get; set; } = "https://ark.cn-beijing.volces.com/api/plan/v3";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "deepseek-v4.1-flash";
    public bool EnableThinking { get; set; } = true;
    public string? ReasoningEffort { get; set; } = "low";
}
