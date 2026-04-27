using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Contract;
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
    private FloatingDockWindow? _floatingDock;
    private WhisperDeskServer? _server;
    private GrpcPipelineClient? _grpcClient;
    private Mutex? _singleInstanceMutex;
    private int _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "WhisperDesk_SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("WhisperDesk is already running.", "WhisperDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName)!;

            // 1. Start backend server (owns Core, STT, LLM, Pipeline, gRPC)
            _server = WhisperDeskServer.Start(exeDir);

            // 2. Build UI services
            _grpcClient = new GrpcPipelineClient(_server.Address);
            var services = new ServiceCollection();
            ConfigureUiServices(services, exeDir, _server.Address, _grpcClient);
            _serviceProvider = services.BuildServiceProvider();

            SetupTrayIcon();

            _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            _mainWindow.Show();
            _mainWindow.IsVisibleChanged += OnMainWindowVisibilityChanged;

            _overlayWindow = new OverlayWindow();
            _overlayWindow.Initialize();
            _overlayWindow.SetPasteService(_serviceProvider.GetRequiredService<ClipboardPasteService>());

            _floatingDock = new FloatingDockWindow();
            _floatingDock.OpenMainWindowRequested += (_, _) => _mainWindow?.ShowFromTray();
            _floatingDock.SingleClicked += OnDockSingleClicked;
            _floatingDock.DockContextMenu = BuildAppMenu();

            var mainVm = _serviceProvider.GetRequiredService<MainViewModel>();
            _floatingDock.PushToTalkStarted += (_, _) => mainVm.BeginPushToTalk();
            _floatingDock.PushToTalkReleased += (_, _) => mainVm.EndPushToTalk();

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

                    _floatingDock?.ApplyStatus(appStatus);

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

            pipeline.SessionCompleted += (_, result) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.ProcessedText))
                    {
                        _floatingDock?.ShowBubble(result.ProcessedText);
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
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start: {ex.Message}", "WhisperDesk Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ConfigureUiServices(IServiceCollection services, string exeDir, string serverAddress, GrpcPipelineClient grpcClient)
    {
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

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new WhisperDesk.Logging.FileLoggerProvider(
                WhisperDesk.Logging.FileLoggerProvider.GetLogPath("ui")));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<IPipelineController>(grpcClient);
        services.AddSingleton(new GrpcDeviceClient(serverAddress));
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<ClipboardPasteService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void OnMainWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_floatingDock == null) return;
        if (_mainWindow?.IsVisible == true)
        {
            _floatingDock.Hide();
        }
        else
        {
            _floatingDock.Show();
        }
    }

    private void OnDockSingleClicked(object? sender, EventArgs e)
    {
        var pipeline = _serviceProvider?.GetService<IPipelineController>();
        var text = pipeline?.LastProcessedText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _floatingDock?.ShowBubble(text);
        }
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

        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _trayIcon.Icon = iconStream != null
            ? new System.Drawing.Icon(iconStream)
            : SystemIcons.Application;

        var contextMenu = BuildAppMenu();

        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => _mainWindow?.ShowFromTray();
    }

    private System.Windows.Controls.ContextMenu BuildAppMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show WhisperDesk" };
        showItem.Click += (_, _) => _mainWindow?.ShowFromTray();
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ExitApplication()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            return;
        }

        var trayIcon = _trayIcon;
        _trayIcon = null;
        trayIcon?.Dispose();

        _overlayWindow?.Close();
        _overlayWindow = null;
        if (_floatingDock != null)
        {
            _floatingDock.Close();
            _floatingDock = null;
        }
        if (_mainWindow != null)
        {
            _mainWindow.IsVisibleChanged -= OnMainWindowVisibilityChanged;
        }
        _mainWindow?.ForceClose();
        _mainWindow = null;

        var serviceProvider = _serviceProvider;
        _serviceProvider = null;
        var grpcClient = _grpcClient;
        _grpcClient = null;
        var server = _server;
        _server = null;
        var singleInstanceMutex = _singleInstanceMutex;
        _singleInstanceMutex = null;

        try
        {
            (serviceProvider as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WhisperDesk] Shutdown error disposing service provider: {ex.Message}");
        }

        try
        {
            grpcClient?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WhisperDesk] Shutdown error disposing gRPC client: {ex.Message}");
        }

        try
        {
            server?.SignalShutdown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WhisperDesk] Shutdown error signaling server: {ex.Message}");
        }

        if (server != null)
        {
            DisposeServerInBackground(server);
        }

        try
        {
            singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WhisperDesk] Shutdown error releasing mutex: {ex.Message}");
        }

        singleInstanceMutex?.Dispose();

        Shutdown();
    }

    private static void DisposeServerInBackground(WhisperDeskServer server)
    {
        var shutdownThread = new Thread(() =>
        {
            try
            {
                server.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WhisperDesk] Background server dispose error: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "WhisperDeskServerShutdown"
        };
        shutdownThread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
