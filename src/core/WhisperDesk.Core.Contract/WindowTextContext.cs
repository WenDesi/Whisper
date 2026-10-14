namespace WhisperDesk.Core.Contract;

public record WindowTextSerializationInfo
{
    public string Selected { get; init; } = string.Empty;
    public string FileFullPath { get; init; } = string.Empty;
    public string DraftText { get; init; } = string.Empty;
    public string OriginalDraftText { get; init; } = string.Empty;
    public IReadOnlyList<DraftCorrectionTurn> DraftCorrections { get; init; } = [];
    public required string MainWindowTitle { get; init; }
}

public record WindowTextContext:WindowTextSerializationInfo{
    public IntPtr WindowHandle { get; init; }
}
