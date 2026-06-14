namespace WhisperDesk.Llm.Provider.Doubao;

public class DoubaoLlmConfig
{
    public string Endpoint { get; set; } = "https://ark.cn-beijing.volces.com/api/v3";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "doubao-seed-2-0-mini-260428";
}
