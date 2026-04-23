using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace WhisperDesk.Server;

public static class GrpcServerHost
{
    public const int Port = 50051;
    public const string Address = "http://localhost:50051";

    public static WebApplication Create(IServiceProvider appServices)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(Port, o => o.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(appServices.GetRequiredService<Core.Pipeline.IPipelineController>());

        var app = builder.Build();
        app.MapGrpcService<PipelineGrpcService>();

        return app;
    }
}
