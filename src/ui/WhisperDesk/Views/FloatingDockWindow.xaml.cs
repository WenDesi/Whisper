using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WhisperDesk.Models;

namespace WhisperDesk.Views;

public partial class FloatingDockWindow : Window
{
    private readonly DispatcherTimer _bubbleHideTimer;
    private readonly DispatcherTimer _longPressTimer;
    private static readonly TimeSpan BubbleAutoHideDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(300);

    private Point _dragStartPoint;
    private bool _isMouseDown;
    private bool _isDragging;
    private bool _longPressActive;
    private DateTime _mouseDownTime;
    private DateTime _lastClickTime = DateTime.MinValue;
    private const int DoubleClickMs = 400;
    private const double DragThreshold = 4.0;

    public event EventHandler? OpenMainWindowRequested;
    public event EventHandler? SingleClicked;
    public event EventHandler? PushToTalkStarted;
    public event EventHandler? PushToTalkReleased;

    private ContextMenu? _dockContextMenu;
    public ContextMenu? DockContextMenu
    {
        get => _dockContextMenu;
        set
        {
            if (_dockContextMenu != null)
            {
                _dockContextMenu.Opened -= OnDockContextMenuOpened;
                _dockContextMenu.Closed -= OnDockContextMenuClosed;
            }

            _dockContextMenu = value;
            DockButton.ContextMenu = value;

            if (value != null)
            {
                value.Opened += OnDockContextMenuOpened;
                value.Closed += OnDockContextMenuClosed;
            }
        }
    }

    private void OnDockContextMenuOpened(object sender, RoutedEventArgs e)
    {
        // WS_EX_NOACTIVATE prevents the popup from receiving focus,
        // so WPF's default outside-click dismiss doesn't fire. Capture
        // the mouse on the menu subtree so any click outside closes it.
        if (sender is ContextMenu menu)
        {
            Mouse.Capture(menu, CaptureMode.SubTree);
        }
    }

    private void OnDockContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && Mouse.Captured == menu)
        {
            Mouse.Capture(null);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public FloatingDockWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        _bubbleHideTimer = new DispatcherTimer { Interval = BubbleAutoHideDelay };
        _bubbleHideTimer.Tick += (_, _) =>
        {
            _bubbleHideTimer.Stop();
            BubblePopup.IsOpen = false;
        };

        _longPressTimer = new DispatcherTimer { Interval = LongPressDelay };
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            if (_isMouseDown && !_isDragging)
            {
                _longPressActive = true;
                PushToTalkStarted?.Invoke(this, EventArgs.Empty);
            }
        };

        PositionAtBottomRight();
    }

    private void PositionAtBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        // Window contains 12px margin around the 56px button — total ~80px.
        Left = workArea.Right - 88;
        Top = workArea.Bottom - 88;
    }

    public void ApplyStatus(AppStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StopAnimations();

            switch (status)
            {
                case AppStatus.Listening:
                    SetRingColor("#FF5252");
                    MicIcon.Visibility = Visibility.Visible;
                    SpinnerPanel.Visibility = Visibility.Collapsed;
                    var pulse = (Storyboard)FindResource("PulseAnimation");
                    pulse.Begin(this, true);
                    break;

                case AppStatus.Transcribing:
                case AppStatus.Cleaning:
                    SetRingColor("#448AFF");
                    MicIcon.Visibility = Visibility.Collapsed;
                    SpinnerPanel.Visibility = Visibility.Visible;
                    var spinner = (Storyboard)FindResource("SpinnerAnimation");
                    spinner.Begin(this, true);
                    break;

                case AppStatus.Ready:
                    SetRingColor("#69F0AE");
                    MicIcon.Visibility = Visibility.Visible;
                    SpinnerPanel.Visibility = Visibility.Collapsed;
                    break;

                case AppStatus.Error:
                    SetRingColor("#FF5252");
                    MicIcon.Visibility = Visibility.Visible;
                    SpinnerPanel.Visibility = Visibility.Collapsed;
                    break;

                default:
                    SetRingColor("#7C4DFF");
                    MicIcon.Visibility = Visibility.Visible;
                    SpinnerPanel.Visibility = Visibility.Collapsed;
                    break;
            }
        });
    }

    public void ShowBubble(string text)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            BubbleViewer.Markdown = text;
            BubblePopup.IsOpen = true;
            RestartBubbleTimer();
        });
    }

    private void RestartBubbleTimer()
    {
        _bubbleHideTimer.Stop();
        _bubbleHideTimer.Interval = BubbleAutoHideDelay;
        _bubbleHideTimer.Start();
    }

    private void OnBubbleMouseEnter(object sender, MouseEventArgs e) => _bubbleHideTimer.Stop();

    private void OnBubbleMouseLeave(object sender, MouseEventArgs e) => RestartBubbleTimer();

    private void OnCloseBubbleClick(object sender, RoutedEventArgs e)
    {
        _bubbleHideTimer.Stop();
        BubblePopup.IsOpen = false;
    }

    private void OnResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        _bubbleHideTimer.Stop();

        var newWidth = BubbleBorder.ActualWidth + e.HorizontalChange;
        newWidth = Math.Max(BubbleBorder.MinWidth, Math.Min(BubbleBorder.MaxWidth, newWidth));
        BubbleBorder.Width = newWidth;

        var newHeight = BubbleViewer.MaxHeight + e.VerticalChange;
        BubbleViewer.MaxHeight = Math.Max(120, Math.Min(800, newHeight));
    }

    private void OnButtonMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = true;
        _isDragging = false;
        _longPressActive = false;
        _dragStartPoint = e.GetPosition(this);
        _mouseDownTime = DateTime.UtcNow;
        DockButton.CaptureMouse();
        _longPressTimer.Start();
    }

    private void OnButtonMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(this);
        if (!_isDragging && !_longPressActive &&
            (Math.Abs(current.X - _dragStartPoint.X) > DragThreshold ||
             Math.Abs(current.Y - _dragStartPoint.Y) > DragThreshold))
        {
            _isDragging = true;
            _longPressTimer.Stop();
            DockButton.ReleaseMouseCapture();
            try { DragMove(); } catch { /* DragMove can throw if button released mid-call */ }
            _isMouseDown = false;
        }
    }

    private void OnButtonMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMouseDown) return;
        _isMouseDown = false;
        _longPressTimer.Stop();
        DockButton.ReleaseMouseCapture();

        if (_isDragging)
        {
            _isDragging = false;
            return;
        }

        if (_longPressActive)
        {
            _longPressActive = false;
            PushToTalkReleased?.Invoke(this, EventArgs.Empty);
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastClickTime).TotalMilliseconds <= DoubleClickMs)
        {
            _lastClickTime = DateTime.MinValue;
            OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _lastClickTime = now;
            SingleClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        // ContextMenu attached on DockButton — WPF opens it automatically
        // and dismisses on outside click. Nothing to do here.
    }

    private void SetRingColor(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        RingBrush.Color = color;
    }

    private void StopAnimations()
    {
        try
        {
            ((Storyboard)FindResource("PulseAnimation")).Stop(this);
            ((Storyboard)FindResource("SpinnerAnimation")).Stop(this);
        }
        catch { }
    }
}
