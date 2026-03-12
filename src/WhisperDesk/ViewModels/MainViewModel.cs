using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperDesk.Models;
using WhisperDesk.Services;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly TranscriptionPipelineService _pipeline;
    private readonly HotkeyService _hotkeyService;
    private readonly ClipboardPasteService _pasteService;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private AppStatus _status = AppStatus.Idle;

    [ObservableProperty]
    private string _statusText = AppStatus.Idle.ToDisplayString();

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    private string _cleanedText = string.Empty;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _lastError = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        TranscriptionPipelineService pipeline,
        HotkeyService hotkeyService,
        ClipboardPasteService pasteService,
        AudioRecorderService recorder)
    {
        _logger = logger;
        _pipeline = pipeline;
        _hotkeyService = hotkeyService;
        _pasteService = pasteService;

        // Wire up events
        _pipeline.StatusChanged += OnStatusChanged;
        _pipeline.TranscriptionCompleted += OnTranscriptionCompleted;
        _pipeline.ErrorOccurred += OnErrorOccurred;

        _hotkeyService.PushToTalkPressed += OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased += OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed += OnPasteHotkeyPressed;

        recorder.AudioLevelChanged += (_, level) =>
        {
            Application.Current?.Dispatcher.Invoke(() => AudioLevel = level);
        };

        // Start hotkey listener
        _hotkeyService.Start();
    }

    private void OnStatusChanged(object? sender, AppStatus status)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Status = status;
            StatusText = status.ToDisplayString();
            IsRecording = status == AppStatus.Listening;
            if (status != AppStatus.Error) HasError = false;
        });
    }

    private void OnTranscriptionCompleted(object? sender, TranscriptionResult result)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RawText = result.RawText;
            CleanedText = result.CleanedText;
        });
    }

    private void OnErrorOccurred(object? sender, string error)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            LastError = error;
            HasError = true;
        });
    }

    private void OnPushToTalkPressed(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (Status == AppStatus.Idle || Status == AppStatus.Ready || Status == AppStatus.Error)
            {
                _cts = new CancellationTokenSource();
                _pipeline.StartRecording();
            }
        });
    }

    private async void OnPushToTalkReleased(object? sender, EventArgs e)
    {
        await Application.Current!.Dispatcher.InvokeAsync(async () =>
        {
            if (Status == AppStatus.Listening)
            {
                await _pipeline.StopRecordingAndProcessAsync(_cts?.Token ?? CancellationToken.None);
            }
        });
    }

    private void OnPasteHotkeyPressed(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_pipeline.LastCleanedText))
        {
            _pasteService.PasteToActiveWindow();
        }
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
        {
            _ = _pipeline.StopRecordingAndProcessAsync(_cts?.Token ?? CancellationToken.None);
        }
        else
        {
            _cts = new CancellationTokenSource();
            _pipeline.StartRecording();
        }
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(CleanedText))
        {
            Clipboard.SetText(CleanedText);
        }
    }

    [RelayCommand]
    private async Task ProcessFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        _cts = new CancellationTokenSource();
        await _pipeline.ProcessFileAsync(filePath, _cts.Token);
    }

    [RelayCommand]
    private void DismissError()
    {
        HasError = false;
        LastError = string.Empty;
    }

    public void Dispose()
    {
        _hotkeyService.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
