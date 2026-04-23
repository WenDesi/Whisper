using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Stt;
using WhisperDesk.Llm;
using WhisperDesk.Core.Models;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Server;
using WhisperDesk.Models;
using WhisperDesk.Services;
using WhisperDesk.ViewModels;
using WhisperDesk.Views;

namespace WhisperDesk;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private OverlayWindow? _overlayWindow;
    private Microsoft.AspNetCore.Builder.WebApplication? _grpcServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Build backend services (Core, STT, LLM, Pipeline)
        var backendServices = new ServiceCollection();
        ConfigureBackendServices(backendServices);
        var backendProvider = backendServices.BuildServiceProvider();

        // 2. Start in-process gRPC server
        _grpcServer = GrpcServerHost.Create(backendProvider);
        _grpcServer.StartAsync().GetAwaiter().GetResult();

        // 3. Build UI services with gRPC client as IPipelineController
        var uiServices = new ServiceCollection();
        ConfigureUiServices(uiServices, backendProvider);
        _serviceProvider = uiServices.BuildServiceProvider();

        SetupTrayIcon();

        _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        _mainWindow.Show();

        // Create overlay window — always visible but transparent until needed
        // No Show/Hide toggling = no focus stealing
        _overlayWindow = new OverlayWindow();
        _overlayWindow.Initialize();
        _overlayWindow.SetPasteService(_serviceProvider.GetRequiredService<ClipboardPasteService>());

        // Wire tray tooltip + overlay updates via IPipelineController
        var pipeline = _serviceProvider.GetRequiredService<IPipelineController>();
        pipeline.StateChanged += (_, pipelineState) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                var appStatus = MapToAppStatus(pipelineState);
                if (_trayIcon != null)
                {
                    _trayIcon.ToolTipText = appStatus.ToTrayTooltip();
                }

                if (appStatus == AppStatus.Idle)
                {
                    _overlayWindow?.HideOverlay();
                }
                else
                {
                    _overlayWindow?.ShowForStatus(appStatus);
                }
            });
        };

        pipeline.ErrorOccurred += (_, error) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _overlayWindow?.ShowForStatus(AppStatus.Error, error.Message);
            });
        };
    }

    private void ConfigureBackendServices(IServiceCollection services)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName)!;
        var config = new ConfigurationBuilder()
            .SetBasePath(exeDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var settings = new WhisperDeskSettings();
        config.Bind(settings);

        var logFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "whisperdesk.log");
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLoggerProvider(logFilePath));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var pipelineConfig = new PipelineConfig
        {
            SttProvider = settings.Transcription.SpeechProvider,
            LlmProvider = settings.Transcription.CleanupProvider,
            Language = settings.Transcription.Language,
            EnableTextCleanup = true,
            AudioDeviceId = settings.Audio.DeviceId,
            Audio = new AudioFormatConfig
            {
                SampleRate = settings.Audio.SampleRate,
                Channels = settings.Audio.Channels,
                BitsPerSample = settings.Audio.BitsPerSample
            }
        };

        services.AddSttProvider(pipelineConfig.SttProvider, config);
        services.AddLlmProvider(pipelineConfig.LlmProvider, config);
        services.AddWhisperDeskPipeline(pipelineConfig, config);
    }

    private void ConfigureUiServices(IServiceCollection services, IServiceProvider backendProvider)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName)!;
        var config = new ConfigurationBuilder()
            .SetBasePath(exeDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var settings = new WhisperDeskSettings();
        config.Bind(settings);

        services.AddSingleton(settings);
        services.AddSingleton(settings.Hotkeys);
        services.AddSingleton(settings.Audio);
        services.AddSingleton(settings.Recording);

        var logFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "whisperdesk.log");
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLoggerProvider(logFilePath));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // IPipelineController via gRPC client
        var client = new GrpcPipelineClient(GrpcServerHost.Address);
        services.AddSingleton<IPipelineController>(client);

        services.AddSingleton<HotkeyService>();
        services.AddSingleton<ClipboardPasteService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private static AppStatus MapToAppStatus(PipelineState state) => state switch
    {
        PipelineState.Idle => AppStatus.Idle,
        PipelineState.Listening => AppStatus.Listening,
        PipelineState.Transcribing => AppStatus.Transcribing,
        PipelineState.PostProcessing => AppStatus.Cleaning,
        PipelineState.Completed => AppStatus.Ready,
        PipelineState.Error => AppStatus.Error,
        _ => AppStatus.Idle
    };

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WhisperDesk - Ready",
            Visibility = Visibility.Visible
        };

        // Use custom app icon for tray (loaded from embedded resource)
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _trayIcon.Icon = iconStream != null
            ? new System.Drawing.Icon(iconStream)
            : SystemIcons.Application;

        // Context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show WhisperDesk" };
        showItem.Click += (_, _) => _mainWindow?.ShowFromTray();
        contextMenu.Items.Add(showItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => _mainWindow?.ShowFromTray();
    }

    private void ExitApplication()
    {
        _trayIcon?.Dispose();
        _overlayWindow?.Close();
        _mainWindow?.ForceClose();

        _grpcServer?.StopAsync().GetAwaiter().GetResult();

        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
