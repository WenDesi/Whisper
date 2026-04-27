namespace WhisperDesk.Core.Contract;

public record WindowTextSerializationInfo
{
    public string Selected { get; init; } = string.Empty;
    public string FileFullPath { get; init; } = string.Empty;
    public required string MainWindowTitle { get; init; }
}

public record WindowTextContext:WindowTextSerializationInfo{
    public IntPtr WindowHandle { get; init; }
}
