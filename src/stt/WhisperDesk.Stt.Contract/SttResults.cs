namespace WhisperDesk.Stt.Contract;

public record SttPartialResult(string Text);

public record SttFinalResult(string Text, TimeSpan Offset, TimeSpan Duration);

public record SttError(string Code, string Message, Exception? Exception = null);
