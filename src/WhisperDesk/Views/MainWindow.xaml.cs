using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.ViewModels;

namespace WhisperDesk.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        using var activity = DiagnosticSources.UI.StartActivity("MainWindow.Constructor");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        using (var initStep = DiagnosticSources.UI.StartActivity("MainWindow.Constructor.InitializeComponent"))
        {
            initStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
            InitializeComponent();
        }

        DataContext = viewModel;

        // Set window icon explicitly so taskbar shows it even when run via dotnet run
        // Use 256x256 size from the ico file for crisp taskbar display
        using (var iconStep = DiagnosticSources.UI.StartActivity("MainWindow.Constructor.LoadIcon"))
        {
            iconStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                using var icon = new System.Drawing.Icon(iconPath, 256, 256);
                Icon = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            // Minimize to tray
            Hide();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of closing
        e.Cancel = true;
        WindowState = WindowState.Minimized;
        Hide();
    }

    public void ShowFromTray()
    {
        using var activity = DiagnosticSources.UI.StartActivity("MainWindow.ShowFromTray");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ForceClose()
    {
        using var activity = DiagnosticSources.UI.StartActivity("MainWindow.ForceClose");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        // Called from tray exit
        Closing -= Window_Closing;
        Close();
    }
}
