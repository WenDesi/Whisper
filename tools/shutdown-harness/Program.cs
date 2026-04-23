using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WhisperDesk.Server;

namespace ShutdownHarness;

public class App : Application
{
    private WhisperDeskServer? _server;
    private GrpcPipelineClient? _grpcClient;
    private readonly string _logPath;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public App()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "harness-wpf-diag.log");
        File.Delete(_logPath);
    }

    private void Log(string msg)
    {
        var line = $"[{_sw.ElapsedMilliseconds}ms] {msg}";
        Console.Error.WriteLine(line);
        File.AppendAllText(_logPath, line + "\n");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log("OnStartup: creating temp config");
        var tempConfig = Path.Combine(Path.GetTempPath(), $"wd-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempConfig);
        File.WriteAllText(Path.Combine(tempConfig, "appsettings.json"), """
        {
            "Transcription": { "SpeechProvider": "AzureSpeech", "CleanupProvider": "AzureOpenAI", "Language": "zh" },
            "AzureSpeech": { "Endpoint": "https://dummy", "SubscriptionKey": "dummy", "Region": "eastus" },
            "AzureOpenAI": { "Endpoint": "https://dummy", "ApiKey": "dummy", "ChatDeployment": "dummy" },
            "Audio": { "SampleRate": 16000, "Channels": 1, "BitsPerSample": 16 }
        }
        """);

        Log("OnStartup: starting server");
        _server = WhisperDeskServer.Start(tempConfig, 50094);
        Log($"OnStartup: server started at {_server.Address}");

        _grpcClient = new GrpcPipelineClient(_server.Address);
        Log("OnStartup: GrpcPipelineClient created");

        // Create a simple window
        var window = new Window { Title = "Shutdown Harness", Width = 300, Height = 100 };
        window.Show();
        Log("OnStartup: window shown");

        // Auto-trigger exit after 3 seconds (simulates user clicking Exit from tray)
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log("Timer: triggering ExitApplication");
            ExitApplication();
        };
        timer.Start();
        Log("OnStartup: exit timer started (3s)");
    }

    private void ExitApplication()
    {
        var exitSw = Stopwatch.StartNew();
        Log("ExitApplication: start");

        Log("ExitApplication: disposing grpcClient");
        _grpcClient?.Dispose();
        Log($"ExitApplication: grpcClient disposed ({exitSw.ElapsedMilliseconds}ms)");

        Log("ExitApplication: calling SignalShutdown");
        _server?.SignalShutdown();
        Log($"ExitApplication: SignalShutdown done ({exitSw.ElapsedMilliseconds}ms)");

        Log("ExitApplication: calling server.Dispose on thread");
        var shutdownThread = new Thread(() =>
        {
            Log("ExitApplication thread: calling server.Dispose()");
            _server?.Dispose();
            Log($"ExitApplication thread: server.Dispose() returned ({exitSw.ElapsedMilliseconds}ms)");
        });
        shutdownThread.Start();
        var joined = shutdownThread.Join(TimeSpan.FromSeconds(5));
        Log($"ExitApplication: thread join returned {joined} ({exitSw.ElapsedMilliseconds}ms)");

        Log($"ExitApplication: calling Shutdown ({exitSw.ElapsedMilliseconds}ms)");
        Shutdown();
    }

    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.Run();

        // Write final timing after app exits
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "harness-wpf-diag.log");
        File.AppendAllText(logPath, $"[FINAL] Process exiting\n");
    }
}
