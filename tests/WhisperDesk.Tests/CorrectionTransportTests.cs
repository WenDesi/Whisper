using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WhisperDesk.Core.Contract;
using WhisperDesk.Llm.Provider.Doubao;
using WhisperDesk.Server;
using Xunit;

namespace WhisperDesk.Tests;

public class CorrectionTransportTests
{
    [Fact]
    public async Task GrpcPreservesTheOriginalAndFullOrderedTimeline()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        using var shutdown = new CancellationTokenSource();
        var pipeline = new Mock<IPipelineController>();
        WindowTextSerializationInfo? received = null;
        pipeline.Setup(p => p.StartSessionAsync(It.IsAny<WindowTextSerializationInfo>(), SessionMode.Instruct,
                It.IsAny<CancellationToken>()))
            .Callback<WindowTextSerializationInfo?, SessionMode, CancellationToken>((context, _, _) => received = context)
            .Returns(Task.CompletedTask);
        builder.Services.AddSingleton(pipeline.Object);
        builder.Services.AddSingleton(shutdown);
        builder.Services.AddGrpc();
        await using var app = builder.Build();
        app.MapGrpcService<PipelineGrpcService>();
        await app.StartAsync();
        await using var client = new GrpcPipelineClient(app.Urls.Single());
        var context = new WindowTextContext
        {
            MainWindowTitle = "Test",
            DraftText = "上一轮的误改结果",
            OriginalDraftText = "原始内容",
            DraftCorrections = Enumerable.Range(1, 12).Select(i => new DraftCorrectionTurn
            {
                RawInstruction = $"第{i}轮 C O R E",
                Output = $"第{i}轮结果",
                Timestamp = DateTimeOffset.Parse("2026-10-14T13:09:00+08:00").AddSeconds(i)
            }).ToArray()
        };

        await client.StartSessionAsync(context, SessionMode.Instruct);

        Assert.NotNull(received);
        Assert.Equal(context.OriginalDraftText, received.OriginalDraftText);
        Assert.Equal(context.DraftText, received.DraftText);
        Assert.Equal(context.DraftCorrections, received.DraftCorrections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DoubaoSendsConfiguredThinkingAndReturnsOnlyFinalText(bool thinking)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        await using var app = builder.Build();
        JsonElement request = default;
        app.MapPost("/chat/completions", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            request = body.RootElement.Clone();
            return Results.Json(new
            {
                id = "test-completion", @object = "chat.completion", created = 1720000000,
                model = "doubao-seed-2.1-pro",
                choices = new[] { new
                {
                    index = 0, finish_reason = "stop",
                    message = new { role = "assistant", content = "Core item", reasoning_content = "Not user-facing text." }
                }}
            });
        });
        await app.StartAsync();
        var provider = new DoubaoLlmProvider(NullLogger<DoubaoLlmProvider>.Instance, new DoubaoLlmConfig
        {
            Endpoint = app.Urls.Single(), ApiKey = "synthetic-test-key", EnableThinking = thinking
        });

        var output = await provider.ProcessTextAsync("test", "test");

        Assert.Equal("Core item", output);
        Assert.Equal("doubao-seed-2.1-pro", request.GetProperty("model").GetString());
        Assert.Equal(thinking ? "enabled" : "disabled",
            request.GetProperty("thinking").GetProperty("type").GetString());
    }
}
