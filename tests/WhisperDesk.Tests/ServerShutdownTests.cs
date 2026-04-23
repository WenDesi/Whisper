using System.Diagnostics;
using System.IO;
using Grpc.Net.Client;
using WhisperDesk.Proto;
using WhisperDesk.Server;
using Xunit;
using Xunit.Abstractions;

namespace WhisperDesk.Tests;

public class ServerShutdownTests : IDisposable
{
    private readonly string _configDir;
    private readonly ITestOutputHelper _output;

    public ServerShutdownTests(ITestOutputHelper output)
    {
        _output = output;
        _configDir = Path.Combine(Path.GetTempPath(), $"whisperdesk-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), """
        {
            "Transcription": { "SpeechProvider": "AzureSpeech", "CleanupProvider": "AzureOpenAI", "Language": "zh" },
            "AzureSpeech": { "Endpoint": "https://dummy", "SubscriptionKey": "dummy", "Region": "eastus" },
            "AzureOpenAI": { "Endpoint": "https://dummy", "ApiKey": "dummy", "ChatDeployment": "dummy" },
            "Audio": { "SampleRate": 16000, "Channels": 1, "BitsPerSample": 16 }
        }
        """);
    }

    /// <summary>
    /// Simulates the exact same sequence as App.ExitApplication:
    /// 1. GrpcPipelineClient with active subscribe loop
    /// 2. Dispose client (cancel subscribe CTS)
    /// 3. SignalShutdown (cancel server shutdown CTS)
    /// 4. Server.Dispose (StopAsync + app dispose)
    /// </summary>
    [Fact]
    public async Task ExitApplication_Sequence_Should_Complete_Within_2_Seconds()
    {
        var sw = Stopwatch.StartNew();

        // Start server (same as App.OnStartup)
        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Starting server...");
        var server = WhisperDeskServer.Start(_configDir, 50098);
        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Server started at {server.Address}");

        // Create GrpcPipelineClient (same as App.ConfigureUiServices)
        var client = new GrpcPipelineClient(server.Address);
        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] GrpcPipelineClient created, subscribe loop running");

        // Wait for subscribe stream to be established on server
        await Task.Delay(1000);
        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Subscribe stream should be established");

        // Now simulate ExitApplication sequence
        var exitSw = Stopwatch.StartNew();

        // Step 1: Dispose gRPC client (cancels subscribe loop)
        _output.WriteLine($"[exit +{exitSw.ElapsedMilliseconds}ms] Disposing GrpcPipelineClient...");
        client.Dispose();
        _output.WriteLine($"[exit +{exitSw.ElapsedMilliseconds}ms] GrpcPipelineClient disposed");

        // Step 2: Signal shutdown (cancels server-side CTS)
        _output.WriteLine($"[exit +{exitSw.ElapsedMilliseconds}ms] Calling SignalShutdown...");
        server.SignalShutdown();
        _output.WriteLine($"[exit +{exitSw.ElapsedMilliseconds}ms] SignalShutdown done");

        // Step 3: Server.Dispose with timeout (same as Task.Run().Wait(5s) in App)
        _output.WriteLine($"[exit +{exitSw.ElapsedMilliseconds}ms] Starting server.Dispose on background thread...");
        var disposeCompleted = Task.Run(() => server.Dispose());
        var completed = await Task.WhenAny(disposeCompleted, Task.Delay(5000));
        _output.WriteLine($"[exit +{exitSw.ElapsedMilliseconds}ms] Server dispose {(completed == disposeCompleted ? "completed" : "TIMED OUT")}");

        exitSw.Stop();
        _output.WriteLine($"[exit +{exitSw.ElapsedMilliseconds}ms] Total exit time");

        Assert.True(exitSw.ElapsedMilliseconds < 2000,
            $"Exit sequence took {exitSw.ElapsedMilliseconds}ms, expected < 2000ms");
        Assert.True(completed == disposeCompleted, "Server.Dispose timed out");
    }

    /// <summary>
    /// Test what happens with ONLY SignalShutdown (no client dispose).
    /// This isolates whether the server-side CTS alone is sufficient.
    /// </summary>
    [Fact]
    public async Task SignalShutdown_Alone_Should_Stop_Subscribe_Stream()
    {
        var server = WhisperDeskServer.Start(_configDir, 50097);
        var client = new GrpcPipelineClient(server.Address);
        await Task.Delay(1000);

        var sw = Stopwatch.StartNew();

        // Only signal shutdown, don't dispose client first
        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] SignalShutdown...");
        server.SignalShutdown();

        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Server.Dispose...");
        var disposeTask = Task.Run(() => server.Dispose());
        var completed = await Task.WhenAny(disposeTask, Task.Delay(5000));
        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {(completed == disposeTask ? "completed" : "TIMED OUT")}");

        client.Dispose();

        Assert.True(completed == disposeTask, $"Server.Dispose timed out ({sw.ElapsedMilliseconds}ms)");
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Shutdown took {sw.ElapsedMilliseconds}ms, expected < 3000ms");
    }

    /// <summary>
    /// Test what happens with ONLY client dispose (no SignalShutdown).
    /// This isolates whether Kestrel detects client disconnect.
    /// </summary>
    [Fact]
    public async Task Client_Dispose_Alone_Should_Allow_Server_Stop()
    {
        var server = WhisperDeskServer.Start(_configDir, 50096);
        var client = new GrpcPipelineClient(server.Address);
        await Task.Delay(1000);

        var sw = Stopwatch.StartNew();

        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Disposing client...");
        client.Dispose();

        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Server.Dispose...");
        var disposeTask = Task.Run(() => server.Dispose());
        var completed = await Task.WhenAny(disposeTask, Task.Delay(5000));
        _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {(completed == disposeTask ? "completed" : "TIMED OUT")}");

        Assert.True(completed == disposeTask, $"Server.Dispose timed out ({sw.ElapsedMilliseconds}ms)");
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Shutdown took {sw.ElapsedMilliseconds}ms, expected < 3000ms");
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, true); } catch { }
    }
}
