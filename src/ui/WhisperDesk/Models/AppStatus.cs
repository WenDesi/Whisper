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
        AppStatus.Idle => "准备就绪",
        AppStatus.Listening => "正在录音",
        AppStatus.Transcribing => "正在转写",
        AppStatus.Cleaning => "正在整理",
        AppStatus.Ready => "文字已就绪",
        AppStatus.Error => "处理未完成",
        _ => "未知状态"
    };

    public static string ToTrayTooltip(this AppStatus status) => status switch
    {
        AppStatus.Idle => "WhisperDesk - 准备就绪",
        AppStatus.Listening => "WhisperDesk - 正在录音",
        AppStatus.Transcribing => "WhisperDesk - 正在转写",
        AppStatus.Cleaning => "WhisperDesk - 正在整理",
        AppStatus.Ready => "WhisperDesk - 文字已就绪",
        AppStatus.Error => "WhisperDesk - 处理未完成",
        _ => "WhisperDesk"
    };
}
