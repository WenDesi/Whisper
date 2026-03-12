namespace WhisperDesk.Models;

public enum AppStatus
{
    Idle,
    Listening,
    Transcribing,
    Cleaning,
    Ready,
    Error
}

public static class AppStatusExtensions
{
    public static string ToDisplayString(this AppStatus status) => status switch
    {
        AppStatus.Idle => "Ready",
        AppStatus.Listening => "🎤 Listening...",
        AppStatus.Transcribing => "📝 Transcribing...",
        AppStatus.Cleaning => "✨ Cleaning up...",
        AppStatus.Ready => "✅ Done - ready to paste",
        AppStatus.Error => "❌ Error",
        _ => "Unknown"
    };

    public static string ToTrayTooltip(this AppStatus status) => status switch
    {
        AppStatus.Idle => "WhisperDesk - Ready",
        AppStatus.Listening => "WhisperDesk - Recording...",
        AppStatus.Transcribing => "WhisperDesk - Transcribing...",
        AppStatus.Cleaning => "WhisperDesk - Cleaning text...",
        AppStatus.Ready => "WhisperDesk - Text ready (paste with hotkey)",
        AppStatus.Error => "WhisperDesk - Error occurred",
        _ => "WhisperDesk"
    };
}
