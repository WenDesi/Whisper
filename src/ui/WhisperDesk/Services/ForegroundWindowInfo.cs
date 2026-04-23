using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WhisperDesk.Services;

public static partial class ForegroundWindowInfo
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static (string ProcessName, string WindowTitle) Get()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return ("", "");

            GetWindowThreadProcessId(hwnd, out var pid);
            var process = Process.GetProcessById((int)pid);
            return (process.ProcessName, process.MainWindowTitle);
        }
        catch
        {
            return ("", "");
        }
    }
}
