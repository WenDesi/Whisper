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
    private readonly AudioWaveform _waveform = new();
    private readonly ScaleTransform[] _waveScales;
    private bool _listening;

    // Win32 window styles to prevent focus stealing
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    // Win32 ShowWindow to avoid focus stealing
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

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
        _waveScales = [WaveScale1, WaveScale2, WaveScale3, WaveScale4, WaveScale5];
        ((Storyboard)FindResource("FadeOut")).Completed += (_, _) =>
        {
            if (RootContainer.Opacity < 0.1)
                StopActiveAnimations();
        };

        // Apply WS_EX_NOACTIVATE after window handle is created
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };

        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            HideOverlay();
        };

        // Start invisible (opacity 0)
        RootContainer.Opacity = 0;
    }

    /// <summary>
    /// Call once at startup to make the window permanently visible (but transparent).
    /// Positions at top-center of screen.
    /// </summary>
    public void Initialize()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        // Initial position at bottom-center of primary screen
        PositionOnCurrentScreen();

        Show();
    }

    public void ShowForStatus(AppStatus status, string? errorMessage = null)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _autoHideTimer?.Stop();
            StopActiveAnimations();
            ResetStatusTextLayout();

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
                    ShowError(errorMessage ?? AppStatus.Error.ToDisplayString());
                    break;
                default:
                    HideOverlay();
                    return;
            }

            // Reposition to cursor's screen when starting to listen
            if (status == AppStatus.Listening)
            {
                PositionOnCurrentScreen();
            }

            // Fade in via opacity — window is always "shown", no Show() call
            if (RootContainer.Opacity < 0.1)
            {
                var fadeIn = (Storyboard)FindResource("FadeIn");
                fadeIn.Begin(this);
            }
        });
    }

    public void ShowDraftPreview(string text, TimeSpan commitDelay)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                HideOverlay();
                return;
            }

            _autoHideTimer?.Stop();
            StopActiveAnimations();

            SetAccentColor("WhisperDesk.Color.Accent");

            WaveformPanel.Visibility = Visibility.Collapsed;
            SpinnerPanel.Visibility = Visibility.Collapsed;
            CheckIcon.Visibility = Visibility.Collapsed;
            ErrorIcon.Visibility = Visibility.Collapsed;

            StatusText.TextWrapping = TextWrapping.Wrap;
            StatusText.MaxWidth = Math.Min(680, Math.Max(220, SystemParameters.WorkArea.Width - 80));
            StatusText.Text = text;
            StartDraftProgress(commitDelay);

            PositionOnCurrentScreen();

            if (RootContainer.Opacity < 0.1)
            {
                var fadeIn = (Storyboard)FindResource("FadeIn");
                fadeIn.Begin(this);
            }
        });
    }

    private void ShowListening()
    {
        SetAccentColor("WhisperDesk.Color.Danger");

        WaveformPanel.Visibility = Visibility.Visible;
        SpinnerPanel.Visibility = Visibility.Collapsed;
        CheckIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = AppStatus.Listening.ToDisplayString();

        _listening = true;
    }

    public void UpdateAudioLevel(float level)
    {
        Dispatcher.VerifyAccess();
        if (!_listening) return;

        _waveform.Push(level);
        var scales = _waveform.Scales;
        for (var i = 0; i < _waveScales.Length; i++)
        {
            if (Math.Abs(_waveScales[i].ScaleY - scales[i]) < 0.001) continue;
            _waveScales[i].BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(scales[i], TimeSpan.FromMilliseconds(50)));
        }
    }

    private void ResetStatusTextLayout()
    {
        StatusText.TextWrapping = TextWrapping.NoWrap;
        StatusText.MaxWidth = double.PositiveInfinity;
        DraftProgressTrack.Visibility = Visibility.Collapsed;
        DraftProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DraftProgressScale.ScaleX = 1;
    }

    private void StartDraftProgress(TimeSpan duration)
    {
        DraftProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DraftProgressScale.ScaleX = 1;
        DraftProgressTrack.Visibility = Visibility.Visible;

        var countdown = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(duration),
            FillBehavior = FillBehavior.HoldEnd
        };
        DraftProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, countdown);
    }

    private void ShowTranscribing()
    {
        SetAccentColor("WhisperDesk.Color.Accent");

        WaveformPanel.Visibility = Visibility.Collapsed;
        SpinnerPanel.Visibility = Visibility.Visible;
        CheckIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = AppStatus.Transcribing.ToDisplayString();

        SetSpinnerColor("WhisperDesk.Color.Accent");
        var spinner = (Storyboard)FindResource("SpinnerAnimation");
        spinner.Begin(this, true);
    }

    private void ShowCleaning()
    {
        SetAccentColor("WhisperDesk.Color.Accent");

        WaveformPanel.Visibility = Visibility.Collapsed;
        SpinnerPanel.Visibility = Visibility.Visible;
        CheckIcon.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = AppStatus.Cleaning.ToDisplayString();

        SetSpinnerColor("WhisperDesk.Color.Accent");
        var spinner = (Storyboard)FindResource("SpinnerAnimation");
        spinner.Begin(this, true);
    }

    private void ShowDone()
    {
        StopActiveAnimations();

        SetAccentColor("WhisperDesk.Color.Success");

        WaveformPanel.Visibility = Visibility.Collapsed;
        SpinnerPanel.Visibility = Visibility.Collapsed;
        CheckIcon.Visibility = Visibility.Visible;
        ErrorIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = AppStatus.Ready.ToDisplayString();

        // Auto-hide after 2 seconds
        // Note: paste is handled by MainViewModel.OnSessionCompleted after clipboard write completes
        _autoHideTimer!.Interval = TimeSpan.FromSeconds(2);
        _autoHideTimer.Start();
    }

    private void ShowError(string message)
    {
        StopActiveAnimations();

        SetAccentColor("WhisperDesk.Color.Danger");

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
        Dispatcher.InvokeAsync(() =>
        {
            _listening = false;
            if (RootContainer.Opacity < 0.1) return;

            var fadeOut = (Storyboard)FindResource("FadeOut");
            fadeOut.Begin(this);
        });
    }

    private void SetAccentColor(string resourceKey)
    {
        BorderAccent.Color = (System.Windows.Media.Color)FindResource(resourceKey);
    }

    private void SetSpinnerColor(string resourceKey)
    {
        var color = (System.Windows.Media.Color)FindResource(resourceKey);
        SpinDot1.Color = color;
        SpinDot2.Color = color;
        SpinDot3.Color = color;
        SpinDot4.Color = color;
    }

    private void StopActiveAnimations()
    {
        _listening = false;
        _waveform.Reset();
        foreach (var scale in _waveScales)
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleY = AudioWaveform.MinimumScale;
        }
        try
        {
            var spinner = (Storyboard)FindResource("SpinnerAnimation");
            spinner.Stop(this);
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

    private void PositionOnCurrentScreen()
    {
        if (!GetCursorPos(out var cursorPt)) return;

        var monitor = MonitorFromPoint(cursorPt, MONITOR_DEFAULTTONEAREST);
        if (monitor == nint.Zero) return;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        double dpiScale = GetDpiScaleForPoint(cursorPt);

        // Work area in logical pixels (excludes taskbar)
        double workLeft = monitorInfo.rcWork.Left / dpiScale;
        double workTop = monitorInfo.rcWork.Top / dpiScale;
        double workWidth = (monitorInfo.rcWork.Right - monitorInfo.rcWork.Left) / dpiScale;
        double workHeight = (monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top) / dpiScale;

        // Bottom-center of the monitor's work area
        Left = workLeft + (workWidth - 200) / 2;
        Top = workTop + workHeight - 110;
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
