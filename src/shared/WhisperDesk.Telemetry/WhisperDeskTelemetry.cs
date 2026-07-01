using System.Diagnostics;

namespace WhisperDesk.Telemetry;

public static class WhisperDeskTelemetry
{
    public const string SourceName = "WhisperDesk";

    public static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
        => Source.StartActivity(name, kind);

    public static Activity? StartActivity(string name, ActivityContext parentContext, ActivityKind kind = ActivityKind.Internal)
        => Source.StartActivity(name, kind, parentContext);
}