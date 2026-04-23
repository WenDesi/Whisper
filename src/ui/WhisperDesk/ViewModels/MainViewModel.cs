using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using WhisperDesk.Core.Contract;
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
    private readonly RecordingSettings _recordingSettings;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly WhisperDeskSettings _appSettings;
    private readonly ILoggerFactory _loggerFactory;
    private CancellationTokenSource? _cts;
    private bool _isStopping;

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
        RecordingSettings recordingSettings,
        AudioDeviceService audioDeviceService,
        WhisperDeskSettings appSettings)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _pipeline = pipeline;
        _hotkeyService = hotkeyService;
        _pasteService = pasteService;
        _recordingSettings = recordingSettings;
        _audioDeviceService = audioDeviceService;
        _appSettings = appSettings;

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
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var appStatus = MapToAppStatus(pipelineState);
            Status = appStatus;
            StatusText = appStatus.ToDisplayString();
            IsRecording = pipelineState == PipelineState.Listening;
            if (appStatus != AppStatus.Error) HasError = false;
        });
    }

    private void OnSessionCompleted(object? sender, PipelineResult result)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            RawText = result.RawTranscript;
            CleanedText = result.ProcessedText;
            PartialText = string.Empty;
            CanSaveRecording = IsSaveRecordingVisible && _pipeline.HasRecordingData;

            // Write to clipboard and paste — run off UI thread to avoid blocking animations
            if (!string.IsNullOrEmpty(result.ProcessedText))
            {
                var textToPaste = result.ProcessedText;
                _ = Task.Run(async () =>
                {
                    // Write to clipboard with retries (must be on STA thread)
                    bool clipboardOk = false;
                    var staThread = new Thread(() =>
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            try
                            {
                                Clipboard.SetDataObject(textToPaste, true);
                                clipboardOk = true;
                                _logger.LogInformation("[ViewModel] Cleaned text copied to clipboard.");
                                break;
                            }
                            catch (System.Runtime.InteropServices.COMException ex)
                            {
                                _logger.LogWarning("[ViewModel] Clipboard busy (attempt {Attempt}/5): {Message}", i + 1, ex.Message);
                                if (i < 4) Thread.Sleep(100);
                            }
                        }
                    });
                    staThread.SetApartmentState(ApartmentState.STA);
                    staThread.Start();
                    staThread.Join();

                    if (clipboardOk)
                    {
                        // Wait for RDP clipboard sync before pasting
                        await Task.Delay(500);
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            _pasteService.PasteToActiveWindow();
                        });
                    }
                    else
                    {
                        _logger.LogWarning("[ViewModel] Clipboard unavailable after retries, skipping auto-paste.");
                    }
                });
            }
        });
    }

    private void OnPipelineError(object? sender, PipelineError error)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            LastError = error.Message;
            HasError = true;
            CanSaveRecording = IsSaveRecordingVisible && _pipeline.HasRecordingData;
        });
    }

    private void OnPartialTranscript(object? sender, string partialText)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            PartialText = partialText;
        });
    }

    private void OnPushToTalkPressed(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Status == AppStatus.Idle || Status == AppStatus.Ready || Status == AppStatus.Error)
            {
                CanSaveRecording = false;
                PartialText = string.Empty;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => _pipeline.StartSessionAsync(_cts.Token));
            }
        });
    }

    private void OnPushToTalkReleased(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Status == AppStatus.Listening && !_isStopping)
            {
                _isStopping = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _pipeline.StopSessionAsync(_cts?.Token ?? CancellationToken.None);
                    }
                    finally
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() => _isStopping = false);
                    }
                });
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
        if (IsRecording)
        {
            if (_isStopping) return;
            _isStopping = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _pipeline.StopSessionAsync(_cts?.Token ?? CancellationToken.None);
                }
                finally
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => _isStopping = false);
                }
            });
        }
        else
        {
            CanSaveRecording = false;
            PartialText = string.Empty;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => _pipeline.StartSessionAsync(_cts.Token));
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
    private async Task OpenSettings()
    {
        try
        {
            var settingsVm = new SettingsViewModel(_audioDeviceService, _appSettings.Audio.DeviceId);
            var settingsDialog = new SettingsDialog { DataContext = settingsVm };

            try
            {
                await DialogHost.Show(settingsDialog, "RootDialog");

                if (settingsVm.Applied && settingsVm.SelectedDeviceId != null)
                {
                    var newDeviceId = settingsVm.SelectedDeviceId;
                    _logger.LogInformation("[ViewModel] Settings applied. Device: {DeviceId}", newDeviceId);

                    // Update in-memory config so next recording session uses the new device
                    _appSettings.Audio.DeviceId = newDeviceId;
                    _appSettings.Audio.DeviceId = newDeviceId;

                    // Persist to appsettings.json
                    SaveDeviceIdToSettings(newDeviceId);
                }
            }
            finally
            {
                settingsVm.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ViewModel] Failed to open settings dialog");
            LastError = $"Failed to open settings: {ex.Message}";
            HasError = true;
        }
    }

    private void SaveDeviceIdToSettings(string deviceId)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;
            var settingsPath = Path.Combine(exeDir, "appsettings.json");

            if (!File.Exists(settingsPath))
            {
                _logger.LogWarning("[ViewModel] appsettings.json not found at {Path}", settingsPath);
                return;
            }

            var json = File.ReadAllText(settingsPath);
            var doc = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
                      ?? new JsonObject();

            // Ensure Audio section exists
            if (doc["Audio"] is not JsonObject audioNode)
            {
                audioNode = new JsonObject();
                doc["Audio"] = audioNode;
            }
            audioNode["DeviceId"] = deviceId;

            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, doc.ToJsonString(writeOptions));

            _logger.LogInformation("[ViewModel] Saved DeviceId to appsettings.json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ViewModel] Failed to save settings to appsettings.json");
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
