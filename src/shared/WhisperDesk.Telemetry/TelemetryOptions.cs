namespace WhisperDesk.Telemetry;

public sealed class TelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public string ServiceName { get; set; } = "WhisperDesk";
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
    public bool LocalSpanLogEnabled { get; set; } = true;
    public string LocalSpanLogFile { get; set; } = "telemetry-spans.log";
    public bool AspNetCoreInstrumentation { get; set; } = true;
    public bool GrpcClientInstrumentation { get; set; } = true;
    public bool HttpClientInstrumentation { get; set; } = true;
}