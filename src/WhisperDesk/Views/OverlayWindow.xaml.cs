using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WhisperDesk.Models;
using WhisperDesk.Services;

namespace WhisperDesk.Views;

public partial class OverlayWindow : Window
{
    private DispatcherTimer? _autoHideTimer;
    private ClipboardPasteService? _pasteService;

    // Win32 window styles to prevent focus stealing
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    // Win32 for caret position, cursor position, and DPI
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    public OverlayWindow()
    {
        InitializeComponent();

        // Apply WS_EX_NOACTIVATE after window handle is created
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            // NOACTIVATE: never steal focus
            // TOOLWINDOW: hide from Alt+Tab
            // TRANSPARENT: click-through
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };

        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            HideOverlay();
        };
    }

    public void SetPasteService(ClipboardPasteService pasteService)
    {
        _pasteService = pasteService;
    }

    public void ShowForStatus(AppStatus status, string? errorMessage = null)
    {
        Dispatcher.Invoke(() =>
        {
            _autoHideTimer?.Stop();
            StopActiveAnimations();

            switch (status)
            {
                case AppStatus.Listening:
                    ShowListening();
                    break;
                case AppStatus.Transcribing:
                    ShowTranscribing();
                    break;
                case AppStatus.Cleaning:
                    ShowCleaning();
                    break;
                case AppStatus.Ready:
                    ShowDone();
                    break;
                case AppStatus.Error:
                    ShowError(errorMessage ?? "Error");
                    break;
                default:
                    HideOverlay();
                    return;
            }

            // Only reposition when first appearing (Listening state)
            if (status == AppStatus.Listening)
            {
                PositionNearCursor();
            }

            if (!IsVisible)
            {
                Show();
                var fadeIn = (Storyboard)FindResource("FadeIn");
                fadeIn.Begin(this);
            }
        });
    }

    private void ShowListening()
    {
        SetAccentColor("#7C4DFF");

        WaveformPanel.Visibility = Visibility.Visible;
        SpinnerPanel.Visibility = Visibility.Collapsed;
        CheckIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = "Listening...";

        var waveform = (Storyboard)FindResource("WaveformAnimation");
        waveform.Begin(this, true);
        var glow = (Storyboard)FindResource("GlowPulse");
        glow.Begin(this, true);
    }

    private void ShowTranscribing()
    {
        SetAccentColor("#448AFF");

        WaveformPanel.Visibility = Visibility.Collapsed;
        SpinnerPanel.Visibility = Visibility.Visible;
        CheckIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = "Transcribing...";

        SetSpinnerColor("#448AFF");
        var spinner = (Storyboard)FindResource("SpinnerAnimation");
        spinner.Begin(this, true);
    }

    private void ShowCleaning()
    {
        SetAccentColor("#00BFA5");

        WaveformPanel.Visibility = Visibility.Collapsed;
        SpinnerPanel.Visibility = Visibility.Visible;
        CheckIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = "Polishing...";

        SetSpinnerColor("#00BFA5");
        var spinner = (Storyboard)FindResource("SpinnerAnimation");
        spinner.Begin(this, true);
    }

    private void ShowDone()
    {
        StopActiveAnimations();

        SetAccentColor("#69F0AE");

        WaveformPanel.Visibility = Visibility.Collapsed;
        SpinnerPanel.Visibility = Visibility.Collapsed;
        CheckIcon.Visibility = Visibility.Visible;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = "Copied!  Ctrl+Shift+V to paste";

        // Auto-hide after 2 seconds
        _autoHideTimer!.Interval = TimeSpan.FromSeconds(2);
        _autoHideTimer.Start();
    }

    private void ShowError(string message)
    {
        StopActiveAnimations();

        SetAccentColor("#FF5252");

        WaveformPanel.Visibility = Visibility.Collapsed;
        SpinnerPanel.Visibility = Visibility.Collapsed;
        CheckIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Visible;

        StatusText.Text = message.Length > 40 ? message[..37] + "..." : message;

        _autoHideTimer!.Interval = TimeSpan.FromSeconds(3);
        _autoHideTimer.Start();
    }

    public void HideOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            if (!IsVisible) return;

            var fadeOut = (Storyboard)FindResource("FadeOut");
            fadeOut.Completed += (_, _) =>
            {
                Hide();
                StopActiveAnimations();
            };
            fadeOut.Begin(this);
        });
    }

    private void SetAccentColor(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        GlowBrush.Color = color;
        BorderAccent.Color = color;
    }

    private void SetSpinnerColor(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        SpinDot1.Color = color;
        SpinDot2.Color = color;
        SpinDot3.Color = color;
        SpinDot4.Color = color;
    }

    private void StopActiveAnimations()
    {
        try
        {
            var waveform = (Storyboard)FindResource("WaveformAnimation");
            waveform.Stop(this);
            var spinner = (Storyboard)FindResource("SpinnerAnimation");
            spinner.Stop(this);
            var glow = (Storyboard)FindResource("GlowPulse");
            glow.Stop(this);
        }
        catch { /* animations may not be started */ }
    }

    private void PositionNearCursor()
    {
        // Try to get text caret (blinking cursor) position first
        if (TryGetCaretScreenPosition(out var caretPt))
        {
            PositionAt(caretPt, offsetY: 4); // just below the caret
            return;
        }

        // Fall back to mouse cursor position
        if (GetCursorPos(out var mousePt))
        {
            PositionAt(mousePt, offsetY: -50); // above the mouse
        }
    }

    private void PositionAt(POINT pt, int offsetY)
    {
        double dpiScale = GetDpiScaleForPoint(pt);
        double logicalX = pt.X / dpiScale;
        double logicalY = pt.Y / dpiScale;

        Left = logicalX + 4;
        Top = logicalY + offsetY / dpiScale;

        // Keep on screen
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        if (Left + ActualWidth > screenWidth)
            Left = logicalX - ActualWidth - 4;
        if (Top < 0)
            Top = logicalY + 24;
        if (Top + ActualHeight > screenHeight)
            Top = screenHeight - ActualHeight - 8;
    }

    private static bool TryGetCaretScreenPosition(out POINT screenPt)
    {
        screenPt = default;

        try
        {
            // Get the foreground window's thread
            var hwndFg = GetForegroundWindow();
            if (hwndFg == nint.Zero) return false;

            uint threadId = GetWindowThreadProcessId(hwndFg, out _);

            var info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf<GUITHREADINFO>();

            if (!GetGUIThreadInfo(threadId, ref info))
                return false;

            // Check if there's a caret
            if (info.hwndCaret == nint.Zero)
                return false;

            // Caret rect is in client coordinates of hwndCaret
            // Use bottom-left of caret rect (where text is being typed)
            var pt = new POINT
            {
                X = info.rcCaret.Left,
                Y = info.rcCaret.Bottom
            };

            // Convert to screen coordinates
            if (!ClientToScreen(info.hwndCaret, ref pt))
                return false;

            screenPt = pt;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double GetDpiScaleForPoint(POINT pt)
    {
        try
        {
            var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (monitor != nint.Zero)
            {
                GetDpiForMonitor(monitor, 0, out uint dpiX, out _);
                return dpiX / 96.0;
            }
        }
        catch { /* fallback below */ }

        return 1.0;
    }
}
