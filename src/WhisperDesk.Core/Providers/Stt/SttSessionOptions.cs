using WhisperDesk.Core.Models;

namespace WhisperDesk.Core.Providers.Stt;

/// <summary>
/// Options for an STT session. Providers use what they support and ignore the rest.
/// </summary>
public class SttSessionOptions
{
    /// <summary>BCP-47 language codes for auto-detection (e.g., ["zh-CN", "en-US"]).</summary>
    public IReadOnlyList<string> Languages { get; init; } = ["zh-CN", "en-US"];

    /// <summary>Phrase hints to improve recognition of specific terms (best-effort per provider).</summary>
    public IReadOnlyList<string> PhraseHints { get; init; } = [];

    /// <summary>Audio format descriptor.</summary>
    public required AudioFormat AudioFormat { get; init; }

    /// <summary>Provider-specific options bag. Keys are provider-defined.</summary>
    public IReadOnlyDictionary<string, object> ProviderOptions { get; init; } = new Dictionary<string, object>();
}
