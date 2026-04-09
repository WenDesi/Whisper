using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using MethodTimer;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Core.Models;
using WhisperDesk.Core.Services;
using WhisperDesk.Models;
using WhisperDesk.Services;
using WhisperDesk.Views;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly IPipelineController _pipeline;
    private readonly HotkeyService _hotkeyService;
    private readonly ClipboardPasteService _pasteService;
    private readonly TranscriptionLogService _logService;
    private readonly RecordingSettings _recordingSettings;
    private readonly ILoggerFactory _loggerFactory;
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
    private string _partialText = string.Empty;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _lastError = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _canSaveRecording;

    [ObservableProperty]
    private bool _isSaveRecordingVisible;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        ILoggerFactory loggerFactory,
        IPipelineController pipeline,
        HotkeyService hotkeyService,
        ClipboardPasteService pasteService,
        TranscriptionLogService logService,
        RecordingSettings recordingSettings)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _pipeline = pipeline;
        _hotkeyService = hotkeyService;
        _pasteService = pasteService;
        _logService = logService;
        _recordingSettings = recordingSettings;

        IsSaveRecordingVisible = !string.IsNullOrWhiteSpace(_recordingSettings.SavePath);

        // Wire pipeline events
        _pipeline.StateChanged += OnPipelineStateChanged;
        _pipeline.SessionCompleted += OnSessionCompleted;
        _pipeline.ErrorOccurred += OnPipelineError;
        _pipeline.PartialTranscriptUpdated += OnPartialTranscript;

        // Wire hotkey events
        _hotkeyService.PushToTalkPressed += OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased += OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed += OnPasteHotkeyPressed;

        _hotkeyService.Start();
    }

    [Time]
    private void OnPipelineStateChanged(object? sender, PipelineState pipelineState)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            var appStatus = MapToAppStatus(pipelineState);
            Status = appStatus;
            StatusText = appStatus.ToDisplayString();
            IsRecording = pipelineState == PipelineState.Listening;
            if (appStatus != AppStatus.Error) HasError = false;
        });
    }

    [Time]
    private void OnSessionCompleted(object? sender, PipelineResult result)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            RawText = result.RawTranscript;
            CleanedText = result.ProcessedText;
            PartialText = string.Empty;
            CanSaveRecording = IsSaveRecordingVisible && _pipeline.HasRecordingData;

            // Copy to clipboard
            if (!string.IsNullOrEmpty(result.ProcessedText))
            {
                Clipboard.SetText(result.ProcessedText);
                _logger.LogInformation("[ViewModel] Cleaned text copied to clipboard.");
            }
        });

        // Log transcription (fire and forget on background)
        _ = _logService.LogTranscriptionAsync(result);
    }

    [Time]
    private void OnPipelineError(object? sender, PipelineError error)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            LastError = error.Message;
            HasError = true;
            CanSaveRecording = IsSaveRecordingVisible && _pipeline.HasRecordingData;
        });
    }

    [Time]
    private void OnPartialTranscript(object? sender, string partialText)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            PartialText = partialText;
        });
    }

    [Time]
    private void OnPushToTalkPressed(object? sender, EventArgs e)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (Status == AppStatus.Idle || Status == AppStatus.Ready || Status == AppStatus.Error)
            {
                CanSaveRecording = false;
                PartialText = string.Empty;
                _cts = new CancellationTokenSource();
                _ = _pipeline.StartSessionAsync(_cts.Token);
            }
        });
    }

    [Time]
    private async void OnPushToTalkReleased(object? sender, EventArgs e)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        await Application.Current!.Dispatcher.InvokeAsync(async () =>
        {
            if (Status == AppStatus.Listening)
            {
                await _pipeline.StopSessionAsync(_cts?.Token ?? CancellationToken.None);
            }
        });
    }

    private void OnPasteHotkeyPressed(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_pipeline.LastProcessedText))
        {
            _pasteService.PasteToActiveWindow();
        }
    }

    [Time]
    [RelayCommand]
    private void ToggleRecording()
    {
        using var _span = MethodTimeLogger.BeginSpan();

        if (IsRecording)
        {
            _ = _pipeline.StopSessionAsync(_cts?.Token ?? CancellationToken.None);
        }
        else
        {
            CanSaveRecording = false;
            PartialText = string.Empty;
            _cts = new CancellationTokenSource();
            _ = _pipeline.StartSessionAsync(_cts.Token);
        }
    }

    [Time]
    [RelayCommand]
    private void CopyToClipboard()
    {
        using var _span = MethodTimeLogger.BeginSpan();

        if (!string.IsNullOrEmpty(CleanedText))
        {
            Clipboard.SetText(CleanedText);
        }
    }

    [Time]
    [RelayCommand]
    private async Task OpenEvalDialog()
    {
        using var _span = MethodTimeLogger.BeginSpan();

        try
        {
            var wavData = _pipeline.GetRecordingAsWav();
            if (wavData == null || wavData.Length == 0)
            {
                _logger.LogWarning("[ViewModel] No recording data for eval");
                return;
            }

            var savePath = _recordingSettings.SavePath;
            if (string.IsNullOrWhiteSpace(savePath))
            {
                _logger.LogWarning("[ViewModel] Recording save path not configured");
                return;
            }

            var evalLogger = _loggerFactory.CreateLogger<EvalDialogViewModel>();
            var evalVm = new EvalDialogViewModel(evalLogger, wavData, RawText, savePath);

            var evalDialog = new EvalDialog { DataContext = evalVm };

            try
            {
                await DialogHost.Show(evalDialog, "RootDialog");
            }
            finally
            {
                evalVm.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ViewModel] Failed to open eval dialog");
            LastError = $"Failed to open eval dialog: {ex.Message}";
            HasError = true;
        }
    }

    [RelayCommand]
    private void DismissError()
    {
        HasError = false;
        LastError = string.Empty;
    }

    /// <summary>Map Core PipelineState to WPF AppStatus for UI compatibility.</summary>
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

    public void Dispose()
    {
        // Unsubscribe pipeline events
        _pipeline.StateChanged -= OnPipelineStateChanged;
        _pipeline.SessionCompleted -= OnSessionCompleted;
        _pipeline.ErrorOccurred -= OnPipelineError;
        _pipeline.PartialTranscriptUpdated -= OnPartialTranscript;

        // Unsubscribe hotkey events
        _hotkeyService.PushToTalkPressed -= OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased -= OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed -= OnPasteHotkeyPressed;

        _hotkeyService.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
