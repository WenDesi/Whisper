public enum CommandType
{
    Replace,
    Append,
    ReadAllContext,
}

public abstract record CommandPayload;
public record ReplaceCommandPayload : CommandPayload
{
    public required string OriginalText { get; init; }
    public required string TargetText { get; init; }
}

public record AppendCommandPayload : CommandPayload
{
    public required string Content { get; init; }
}

public record ReadAllContextCommandPayload : CommandPayload;

public record CommandEvent
{
    public string CommandId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public required CommandType CommandType { get; init; }
    public required CommandPayload Payload { get; init; }
}

public record CommandResultBodyCommon{}
public record TextCommandResult : CommandResultBodyCommon
{
    public required string Result { get; init; }
}

public record CommandResult
{
    public required string CommandId { get; init; }
    public required CommandResultBodyCommon Result { get; init; }
}
