namespace WhisperDesk.Stt.Contract;

public record DialogTurn(string Role, string Text);

public class SttSessionOptions
{
    public IReadOnlyList<string> Languages { get; init; } = ["zh-CN", "en-US"];
    public IReadOnlyList<string> PhraseHints { get; init; } = [];
    public IReadOnlyList<DialogTurn> DialogContext { get; init; } = [];
    public required AudioFormat AudioFormat { get; init; }
    public IReadOnlyDictionary<string, object> ProviderOptions { get; init; } = new Dictionary<string, object>();
}
