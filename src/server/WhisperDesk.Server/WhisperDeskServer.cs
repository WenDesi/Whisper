using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Stt;
using WhisperDesk.Llm;

namespace WhisperDesk.Server;

public class WhisperDeskServer : IDisposable
{
    private readonly WebApplication _app;

    public string Address { get; }

    private WhisperDeskServer(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public static WhisperDeskServer Start(string configBasePath, int port = 50051)
    {
        var address = $"http://localhost:{port}";

        var config = new ConfigurationBuilder()
            .SetBasePath(configBasePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var pipelineConfig = BuildPipelineConfig(config);

        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port, o => o.Protocols = HttpProtocols.Http2);
        });

        var logFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "whisperdesk.log");

        builder.Services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Debug);
        });

        builder.Services.AddSttProvider(pipelineConfig.SttProvider, config);
        builder.Services.AddLlmProvider(pipelineConfig.LlmProvider, config);
        builder.Services.AddWhisperDeskPipeline(pipelineConfig, config);

        builder.Services.AddGrpc();

        var app = builder.Build();
        app.MapGrpcService<PipelineGrpcService>();
        app.MapGrpcService<DeviceGrpcService>();

        app.StartAsync().GetAwaiter().GetResult();

        return new WhisperDeskServer(app, address);
    }

    private static PipelineConfig BuildPipelineConfig(IConfiguration config)
    {
        var sttProvider = config["Transcription:SpeechProvider"] ?? "AzureSpeech";
        var llmProvider = config["Transcription:CleanupProvider"] ?? "AzureOpenAI";
        var language = config["Transcription:Language"] ?? "zh";
        var deviceId = config["Audio:DeviceId"] ?? "";

        int.TryParse(config["Audio:SampleRate"], out var sampleRate);
        if (sampleRate == 0) sampleRate = 16000;
        int.TryParse(config["Audio:Channels"], out var channels);
        if (channels == 0) channels = 1;
        int.TryParse(config["Audio:BitsPerSample"], out var bitsPerSample);
        if (bitsPerSample == 0) bitsPerSample = 16;

        return new PipelineConfig
        {
            SttProvider = sttProvider,
            LlmProvider = llmProvider,
            Language = language,
            EnableTextCleanup = true,
            AudioDeviceId = deviceId,
            Audio = new AudioFormatConfig
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = bitsPerSample
            }
        };
    }

    public void Stop()
    {
        _app.StopAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Stop();
        (_app as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
