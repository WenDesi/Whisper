using System.Windows;
using WhisperDesk.ViewModels;

namespace WhisperDesk.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ForceClose()
    {
        // Called from tray exit
        Closing -= Window_Closing;
        Close();
    }
}
