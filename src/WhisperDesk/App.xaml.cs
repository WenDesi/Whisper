using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        SetupTrayIcon();

        _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        _mainWindow.Show();

        // Create overlay window — always visible but transparent until needed
        // No Show/Hide toggling = no focus stealing
        _overlayWindow = new OverlayWindow();
        _overlayWindow.Initialize();
        _overlayWindow.SetPasteService(_serviceProvider.GetRequiredService<ClipboardPasteService>());

        // Wire tray tooltip + overlay updates
        var pipeline = _serviceProvider.GetRequiredService<TranscriptionPipelineService>();
        pipeline.StatusChanged += (_, status) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_trayIcon != null)
                {
                    _trayIcon.ToolTipText = status.ToTrayTooltip();
                }

                // Show/hide overlay based on status
                if (status == AppStatus.Idle)
                {
                    _overlayWindow?.HideOverlay();
                }
                else
                {
                    _overlayWindow?.ShowForStatus(status);
                }
            });
        };

        pipeline.ErrorOccurred += (_, error) =>
        {
            Dispatcher.Invoke(() =>
            {
                _overlayWindow?.ShowForStatus(AppStatus.Error, error);
            });
        };
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Configuration
        // For single-file publish, AppContext.BaseDirectory points to the temp extraction dir.
        // Use the exe's actual location so appsettings.json sits next to the exe.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName)!;
        var config = new ConfigurationBuilder()
            .SetBasePath(exeDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var settings = new WhisperDeskSettings();
        config.Bind(settings);

        // Register settings
        services.AddSingleton(settings);
        services.AddSingleton(settings.AzureOpenAI);
        services.AddSingleton(settings.AzureSpeech);
        services.AddSingleton(settings.Hotkeys);
        services.AddSingleton(settings.Audio);
        services.AddSingleton(settings.Transcription);
        services.AddSingleton(settings.Recording);

        // Logging
        var logFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "whisperdesk.log");
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLoggerProvider(logFilePath));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Register concrete service types (needed for DI resolution)
        services.AddSingleton<AzureSpeechService>();
        services.AddSingleton<AzureOpenAIService>();

        // Register STT provider based on config
        var sttProvider = settings.Transcription.SpeechProvider.ToLowerInvariant();
        switch (sttProvider)
        {
            case "azurespeech":
                services.AddSingleton<ISpeechToTextService>(sp => sp.GetRequiredService<AzureSpeechService>());
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown SpeechProvider '{settings.Transcription.SpeechProvider}'. Use 'AzureSpeech'.");
        }

        // Register text cleanup provider based on config
        var cleanupProvider = settings.Transcription.CleanupProvider.ToLowerInvariant();
        switch (cleanupProvider)
        {
            case "azureopenai":
                services.AddSingleton<ITextCleanupService>(sp => sp.GetRequiredService<AzureOpenAIService>());
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown CleanupProvider '{settings.Transcription.CleanupProvider}'. Use 'AzureOpenAI'.");
        }

        // Services
        services.AddSingleton<AudioRecorderService>();
        services.AddSingleton<TranscriptionLogService>();
        services.AddSingleton<TranscriptionPipelineService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<ClipboardPasteService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }

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
