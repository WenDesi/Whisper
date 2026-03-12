using System.Drawing;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        SetupTrayIcon();

        _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        _mainWindow.Show();

        // Wire tray tooltip updates
        var pipeline = _serviceProvider.GetRequiredService<TranscriptionPipelineService>();
        pipeline.StatusChanged += (_, status) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_trayIcon != null)
                {
                    _trayIcon.ToolTipText = status.ToTrayTooltip();
                }
            });
        };
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Configuration
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var settings = new WhisperDeskSettings();
        config.Bind(settings);

        // Register settings
        services.AddSingleton(settings);
        services.AddSingleton(settings.AzureOpenAI);
        services.AddSingleton(settings.Hotkeys);
        services.AddSingleton(settings.Audio);
        services.AddSingleton(settings.Transcription);

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Services
        services.AddSingleton<AudioRecorderService>();
        services.AddSingleton<AzureWhisperService>();
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

        // Use default app icon
        _trayIcon.Icon = SystemIcons.Application;

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
