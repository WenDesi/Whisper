using System.Diagnostics;

namespace WhisperDesk.Core.Diagnostics;

/// <summary>
/// Centralized ActivitySource definitions for OpenTelemetry tracing.
/// Each logical component gets its own source for selective enablement.
/// </summary>
public static class DiagnosticSources
{
    public const string PipelineName = "WhisperDesk.Pipeline";
    public const string AudioName = "WhisperDesk.Audio";
    public const string SttName = "WhisperDesk.Stt";
    public const string LlmName = "WhisperDesk.Llm";
    public const string UiName = "WhisperDesk.UI";

    public static readonly ActivitySource Pipeline = new(PipelineName, "1.0.0");
    public static readonly ActivitySource Audio = new(AudioName, "1.0.0");
    public static readonly ActivitySource Stt = new(SttName, "1.0.0");
    public static readonly ActivitySource Llm = new(LlmName, "1.0.0");
    public static readonly ActivitySource UI = new(UiName, "1.0.0");

    /// <summary>All source names, for convenient listener/provider registration.</summary>
    public static readonly string[] AllSourceNames =
    [
        PipelineName, AudioName, SttName, LlmName, UiName
    ];
}
