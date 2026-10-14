namespace WhisperDesk.Core.Contract;

public sealed record DraftCorrectionTurn
{
    public required string RawInstruction { get; init; }
    public required string Output { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
