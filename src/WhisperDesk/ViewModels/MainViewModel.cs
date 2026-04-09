using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
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

    private void OnPipelineStateChanged(object? sender, PipelineState pipelineState)
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.OnStateChanged.DispatcherInvoke");
        activity?.SetTag("calling.thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("pipeline.state", pipelineState.ToString());

        Application.Current?.Dispatcher.Invoke(() =>
        {
            activity?.SetTag("ui.thread.id", Environment.CurrentManagedThreadId);

            var appStatus = MapToAppStatus(pipelineState);
            Status = appStatus;
            StatusText = appStatus.ToDisplayString();
            IsRecording = pipelineState == PipelineState.Listening;
            if (appStatus != AppStatus.Error) HasError = false;
        });
    }

    private void OnSessionCompleted(object? sender, PipelineResult result)
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.OnSessionCompleted.DispatcherInvoke");
        activity?.SetTag("calling.thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("result.raw.length", result.RawTranscript?.Length ?? 0);
        activity?.SetTag("result.processed.length", result.ProcessedText?.Length ?? 0);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            activity?.SetTag("ui.thread.id", Environment.CurrentManagedThreadId);

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

    private void OnPipelineError(object? sender, PipelineError error)
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.OnPipelineError.DispatcherInvoke");
        activity?.SetTag("calling.thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("error.stage", error.Stage);
        activity?.SetTag("error.message", error.Message);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            activity?.SetTag("ui.thread.id", Environment.CurrentManagedThreadId);

            LastError = error.Message;
            HasError = true;
            CanSaveRecording = IsSaveRecordingVisible && _pipeline.HasRecordingData;
        });
    }

    private void OnPartialTranscript(object? sender, string partialText)
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.OnPartialTranscript.DispatcherInvoke");
        activity?.SetTag("calling.thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("partial.length", partialText?.Length ?? 0);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            activity?.SetTag("ui.thread.id", Environment.CurrentManagedThreadId);
            PartialText = partialText;
        });
    }

    private void OnPushToTalkPressed(object? sender, EventArgs e)
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.OnPushToTalkPressed.DispatcherInvoke");
        activity?.SetTag("calling.thread.id", Environment.CurrentManagedThreadId);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            activity?.SetTag("ui.thread.id", Environment.CurrentManagedThreadId);

            if (Status == AppStatus.Idle || Status == AppStatus.Ready || Status == AppStatus.Error)
            {
                CanSaveRecording = false;
                PartialText = string.Empty;
                _cts = new CancellationTokenSource();
                _ = _pipeline.StartSessionAsync(_cts.Token);
            }
        });
    }

    private async void OnPushToTalkReleased(object? sender, EventArgs e)
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.OnPushToTalkReleased.DispatcherInvoke");
        activity?.SetTag("calling.thread.id", Environment.CurrentManagedThreadId);

        await Application.Current!.Dispatcher.InvokeAsync(async () =>
        {
            activity?.SetTag("ui.thread.id", Environment.CurrentManagedThreadId);

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

    [RelayCommand]
    private void ToggleRecording()
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.ToggleRecording");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("is_recording", IsRecording);

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

    [RelayCommand]
    private void CopyToClipboard()
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.CopyToClipboard");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("text.length", CleanedText?.Length ?? 0);

        if (!string.IsNullOrEmpty(CleanedText))
        {
            Clipboard.SetText(CleanedText);
        }
    }

    [RelayCommand]
    private async Task OpenEvalDialog()
    {
        using var activity = DiagnosticSources.UI.StartActivity("ViewModel.OpenEvalDialog");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        try
        {
            var wavData = _pipeline.GetRecordingAsWav();
            if (wavData == null || wavData.Length == 0)
            {
                _logger.LogWarning("[ViewModel] No recording data for eval");
                activity?.SetTag("result", "no_recording_data");
                return;
            }

            var savePath = _recordingSettings.SavePath;
            if (string.IsNullOrWhiteSpace(savePath))
            {
                _logger.LogWarning("[ViewModel] Recording save path not configured");
                activity?.SetTag("result", "no_save_path");
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
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
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
