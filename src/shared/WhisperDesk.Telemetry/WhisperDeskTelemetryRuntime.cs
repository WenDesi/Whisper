using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace WhisperDesk.Telemetry;

public sealed class WhisperDeskTelemetryRuntime : IDisposable
{
    private readonly TracerProvider? _tracerProvider;

    private WhisperDeskTelemetryRuntime(TracerProvider? tracerProvider)
    {
        _tracerProvider = tracerProvider;
    }

    public static WhisperDeskTelemetryRuntime Start(IConfiguration configuration)
    {
        var options = new TelemetryOptions();
        configuration.GetSection("Telemetry").Bind(options);

        if (!options.Enabled)
        {
            return new WhisperDeskTelemetryRuntime(null);
        }

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(options.ServiceName)
                .AddAttributes([
                    new KeyValuePair<string, object>("service.instance.id", Environment.MachineName),
                    new KeyValuePair<string, object>("process.runtime.name", ".NET")
                ]))
            .AddSource(WhisperDeskTelemetry.SourceName);

        if (options.AspNetCoreInstrumentation)
        {
            builder.AddAspNetCoreInstrumentation();
        }

        if (options.GrpcClientInstrumentation)
        {
            builder.AddGrpcClientInstrumentation();
        }

        if (options.HttpClientInstrumentation)
        {
            builder.AddHttpClientInstrumentation();
        }

        if (options.LocalSpanLogEnabled)
        {
            var localSpanLogPath = ResolveLocalSpanLogPath(options.LocalSpanLogFile);
            builder.AddProcessor(new SimpleActivityExportProcessor(new ActivityFileExporter(localSpanLogPath)));
        }

        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint) && Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var endpoint))
        {
            builder.AddOtlpExporter(exporterOptions =>
            {
                exporterOptions.Endpoint = endpoint;
                exporterOptions.Protocol = OtlpExportProtocol.Grpc;
            });
        }

        return new WhisperDeskTelemetryRuntime(builder.Build());
    }

    public void Dispose()
    {
        _tracerProvider?.Dispose();
    }

    private static string ResolveLocalSpanLogPath(string configuredPath)
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk",
            "logs");

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(logsDir, configuredPath);
    }
}