namespace WhisperDesk.Core.Providers.Stt;

/// <summary>Partial (interim) recognition result — may change as more audio arrives.</summary>
public record SttPartialResult(string Text);

/// <summary>Finalized recognition result — this segment is complete and won't change.</summary>
public record SttFinalResult(string Text, TimeSpan Offset, TimeSpan Duration);

/// <summary>Error from the STT provider.</summary>
public record SttError(string Code, string Message, Exception? Exception = null);
