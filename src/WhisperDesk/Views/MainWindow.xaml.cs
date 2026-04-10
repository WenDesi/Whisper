using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WhisperDesk.ViewModels;

namespace WhisperDesk.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Set window icon explicitly so taskbar shows it even when run via dotnet run
        // Use 256x256 size from the ico file for crisp taskbar display
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Intercept WM_SYSCOMMAND SC_KEYMENU at the Win32 message level.
        // This suppresses menu-bar activation triggered by bare Alt press/release,
        // which WPF (via ComponentDispatcher) and DefWindowProc can generate even
        // when the low-level keyboard hook has already marked the Alt keys as handled.
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // SC_KEYMENU with lParam==0 is a keyboard-triggered menu activation (bare Alt key).
        // Masking wParam with 0xFFF0 normalizes the value per the Win32 spec.
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_KEYMENU)
        {
            handled = true;
        }
        return IntPtr.Zero;
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
