using System.Collections.Concurrent;
using WhisperDesk.Stt.Contract;

namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// Thread-safe builder that aggregates context from multiple IContextProviders.
/// Merged into SttSessionOptions before the STT session starts.
/// </summary>
public class SessionContextBuilder
{
    private readonly ConcurrentBag<string> _phraseHints = new();
    private readonly ConcurrentBag<string> _languages = new();
    private readonly ConcurrentDictionary<string, object> _metadata = new();
    private readonly ConcurrentBag<(string Role, string Text)> _dialogTurns = new();

    /// <summary>Add phrase hints (hot words, technical terms) for improved recognition.</summary>
    public void AddPhraseHints(IEnumerable<string> hints)
    {
        foreach (var hint in hints)
            _phraseHints.Add(hint);
    }

    /// <summary>Add a single phrase hint.</summary>
    public void AddPhraseHint(string hint) => _phraseHints.Add(hint);

    /// <summary>Add preferred languages (BCP-47 codes, e.g. "zh-CN", "en-US").</summary>
    public void AddLanguages(IEnumerable<string> languages)
    {
        foreach (var lang in languages)
            _languages.Add(lang);
    }

    /// <summary>Store arbitrary metadata for downstream consumption.</summary>
    public void SetMetadata(string key, object value) => _metadata[key] = value;

    /// <summary>Retrieve metadata by key.</summary>
    public T? GetMetadata<T>(string key) where T : class
        => _metadata.TryGetValue(key, out var val) ? val as T : null;

    /// <summary>Snapshot the collected phrase hints.</summary>
    public IReadOnlyList<string> PhraseHints => _phraseHints.ToArray();

    /// <summary>Snapshot the collected languages.</summary>
    public IReadOnlyList<string> Languages => _languages.ToArray();

    /// <summary>Add a dialog turn (role + text) for dialog context.</summary>
    public void AddDialogTurn(string role, string text) => _dialogTurns.Add((role, text));

    /// <summary>Snapshot the collected dialog turns.</summary>
    public IReadOnlyList<DialogTurn> DialogTurns => _dialogTurns.Select(t => new DialogTurn(t.Role, t.Text)).ToArray();
}
