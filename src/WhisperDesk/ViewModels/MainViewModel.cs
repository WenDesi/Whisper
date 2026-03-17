using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using WhisperDesk.Models;
using WhisperDesk.Services;
using WhisperDesk.Views;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly TranscriptionPipelineService _pipeline;
    private readonly HotkeyService _hotkeyService;
    private readonly ClipboardPasteService _pasteService;
    private readonly AzureSpeechService _speechService;
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
        TranscriptionPipelineService pipeline,
        HotkeyService hotkeyService,
        ClipboardPasteService pasteService,
        AzureSpeechService speechService,
        RecordingSettings recordingSettings)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _pipeline = pipeline;
        _hotkeyService = hotkeyService;
        _pasteService = pasteService;
        _speechService = speechService;
        _recordingSettings = recordingSettings;

        // Show save button only if save path is configured
        IsSaveRecordingVisible = !string.IsNullOrWhiteSpace(_recordingSettings.SavePath);

        // Wire up events
        _pipeline.StatusChanged += OnStatusChanged;
        _pipeline.TranscriptionCompleted += OnTranscriptionCompleted;
        _pipeline.ErrorOccurred += OnErrorOccurred;

        _hotkeyService.PushToTalkPressed += OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased += OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed += OnPasteHotkeyPressed;

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
            CanSaveRecording = IsSaveRecordingVisible && _speechService.HasRecordingData;
        });
    }

    private void OnErrorOccurred(object? sender, string error)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            LastError = error;
            HasError = true;
            // Still allow eval if audio was captured before the error
            CanSaveRecording = IsSaveRecordingVisible && _speechService.HasRecordingData;
        });
    }

    private void OnPushToTalkPressed(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (Status == AppStatus.Idle || Status == AppStatus.Ready || Status == AppStatus.Error)
            {
                CanSaveRecording = false;
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
            CanSaveRecording = false;
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
    private async Task OpenEvalDialog()
    {
        try
        {
            var wavData = _speechService.GetRecordingAsWav();
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

            // Create the eval dialog viewmodel
            var evalLogger = _loggerFactory.CreateLogger<EvalDialogViewModel>();
            var evalVm = new EvalDialogViewModel(evalLogger, wavData, RawText, savePath);

            // Create the dialog view and bind it
            var evalDialog = new EvalDialog { DataContext = evalVm };

            // Show via MaterialDesign DialogHost
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
