using System.Collections.Concurrent;
using Grpc.Core;
using Moq;
using Moq.Protected;
using WhisperDesk.Core.Contract;
using WhisperDesk.Proto;
using WhisperDesk.Server;
using Xunit;

namespace WhisperDesk.Tests;

public class AudioLevelStreamTests
{
    [Fact]
    public async Task SlowReaderGetsTheLatestLevelInsteadOfAnAccumulatedQueue()
    {
        using var shutdown = new CancellationTokenSource();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var level = 0.1f;
        var reads = 0;
        var pipeline = new Mock<IPipelineController>();
        pipeline.SetupGet(p => p.AudioLevel).Returns(() =>
        {
            Interlocked.Increment(ref reads);
            return Volatile.Read(ref level);
        });
        var firstWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new ConcurrentQueue<float>();
        var writer = new TestWriter(async (message, ct) =>
        {
            samples.Enqueue(message.Level);
            if (samples.Count == 1)
            {
                firstWrite.TrySetResult(true);
                await allowWrite.Task.WaitAsync(ct);
            }
            else
            {
                secondWrite.TrySetResult(true);
            }
        });
        var service = new PipelineGrpcService(pipeline.Object, shutdown);
        var streaming = service.SubscribeAudioLevel(new SubscribeRequest(), writer, Context(request.Token));

        await firstWrite.Task.WaitAsync(request.Token);
        Volatile.Write(ref level, 0.4f);
        await Task.Delay(160, request.Token);
        Assert.Equal(1, Volatile.Read(ref reads));
        Volatile.Write(ref level, 0.9f);
        allowWrite.SetResult(true);
        await secondWrite.Task.WaitAsync(request.Token);
        request.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(2));

        var values = samples.ToArray();
        Assert.Equal(0.1f, values[0]);
        Assert.Equal(0.9f, values[1]);
    }

    [Fact]
    public async Task SilenceDoesNotGenerateContinuousNetworkTraffic()
    {
        using var shutdown = new CancellationTokenSource();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pipeline = new Mock<IPipelineController>();
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var writer = new TestWriter((message, _) =>
        {
            Assert.Equal(0, message.Level);
            Interlocked.Increment(ref count);
            sent.TrySetResult(true);
            return Task.CompletedTask;
        });
        var service = new PipelineGrpcService(pipeline.Object, shutdown);
        var streaming = service.SubscribeAudioLevel(new SubscribeRequest(), writer, Context(request.Token));
        await sent.Task.WaitAsync(request.Token);
        await Task.Delay(180, request.Token);
        shutdown.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task ShutdownCancelsABlockedStreamWrite()
    {
        using var shutdown = new CancellationTokenSource();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pipeline = new Mock<IPipelineController>();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new TestWriter(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var service = new PipelineGrpcService(pipeline.Object, shutdown);
        var streaming = service.SubscribeAudioLevel(new SubscribeRequest(), writer, Context(request.Token));
        await entered.Task.WaitAsync(request.Token);
        shutdown.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static ServerCallContext Context(CancellationToken ct)
    {
        var context = new Mock<ServerCallContext>();
        context.Protected().SetupGet<CancellationToken>("CancellationTokenCore").Returns(ct);
        return context.Object;
    }

    private sealed class TestWriter(Func<AudioLevelEvent, CancellationToken, Task> write)
        : IServerStreamWriter<AudioLevelEvent>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(AudioLevelEvent message) => write(message, CancellationToken.None);
        public Task WriteAsync(AudioLevelEvent message, CancellationToken ct) => write(message, ct);
    }
}
