using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using WhisperDesk.Core.Contract;
using WhisperDesk.Server;
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
    private readonly GrpcDeviceClient _deviceClient;
    private readonly WhisperDeskSettings _appSettings;
    private CancellationTokenSource? _cts;
    private bool _isStopping;
    private WindowTextContext? _sessionTextContext;

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

    public string PushToTalkHint => $"\U0001f3a4 Hold {_appSettings.Hotkeys.PushToTalk} to record";
    public string PasteHint => $"\U0001f4cb Press {_appSettings.Hotkeys.PasteTranscription} to paste";

    public MainViewModel(
        ILogger<MainViewModel> logger,
        IPipelineController pipeline,
        HotkeyService hotkeyService,
        ClipboardPasteService pasteService,
        GrpcDeviceClient deviceClient,
        WhisperDeskSettings appSettings)
    {
        _logger = logger;
        _pipeline = pipeline;
        _hotkeyService = hotkeyService;
        _pasteService = pasteService;
        _deviceClient = deviceClient;
        _appSettings = appSettings;

        // Wire pipeline events
        _pipeline.StateChanged += OnPipelineStateChanged;
        _pipeline.SessionCompleted += OnSessionCompleted;
        _pipeline.ErrorOccurred += OnPipelineError;
        _pipeline.PartialTranscriptUpdated += OnPartialTranscript;
        _pipeline.LocalCommandExecuted += OnLocalCommandExecuted;

        // Wire hotkey events
        _hotkeyService.PushToTalkPressed += OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased += OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed += OnPasteHotkeyPressed;

        _hotkeyService.Start();
        ForegroundWindowInfo.Configure(_logger);
    }

    private void OnLocalCommandExecuted(object? sender, CommandEvent cmd)
    {
        var capturedContext = _sessionTextContext;
        if (capturedContext is null) return;

        var staThread = new Thread(() =>
        {
            string result = cmd switch
            {
                { CommandType: CommandType.Append, Payload: AppendCommandPayload a } =>
                    ForegroundWindowInfo.Append(capturedContext, a.Content),
                { CommandType: CommandType.Replace, Payload: ReplaceCommandPayload r } =>
                    ForegroundWindowInfo.Replace(capturedContext, r.OriginalText, r.TargetText),
                { CommandType: CommandType.ReadAllContext } =>
                    ForegroundWindowInfo.ReadAllContext(capturedContext),
                _ => ForegroundWindowInfo.ErrorNotSupported
            };
            _logger.LogInformation("[ViewModel] Command {Type} result: {Result}", cmd.CommandType, result);
            _pipeline.SendCommandResult(new CommandResult
            {
                CommandId = cmd.CommandId,
                Result = new TextCommandResult { Result = result }
            });
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
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

            _logger.LogInformation("[ViewModel] Session completed. Raw: {RawLen} chars, Cleaned: {CleanLen} chars.",
                RawText.Length, CleanedText.Length);
            // if (!string.IsNullOrEmpty(result.ProcessedText))
            // {
            //     var textToPaste = result.ProcessedText;
            //     var capturedContext = _sessionTextContext;
            //     if (capturedContext is { } ctx)
            //     {
            //         var staThread = new Thread(() =>
            //         {
            //             var replaceResult = ForegroundWindowInfo.Append(ctx,  textToPaste);
            //             if (replaceResult == ForegroundWindowInfo.Success)
            //                 _logger.LogInformation("[ViewModel] Text inserted via Append.");
            //             else
            //                 _logger.LogWarning("[ViewModel] Append failed: {Reason}. Selected='{Selected}', Window={Handle}",
            //                     replaceResult, ctx.Selected, ctx.WindowHandle);
            //         });
            //         staThread.SetApartmentState(ApartmentState.STA);
            //         staThread.IsBackground = true;
            //         staThread.Start();
            //     }
            //     else
            //     {
            //         _logger.LogWarning("[ViewModel] No session context captured, text not inserted.");
            //     }
                
            // }
        });
    }

    private void OnPipelineError(object? sender, PipelineError error)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            LastError = error.Message;
            HasError = true;
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
                PartialText = string.Empty;
                _sessionTextContext = ForegroundWindowInfo.GetTextContext();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => _pipeline.StartSessionAsync(_sessionTextContext, _cts.Token));
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

    private async void OnPasteHotkeyPressed(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_pipeline.LastProcessedText))
        {
            _logger.LogDebug("[ViewModel] Paste hotkey pressed, invoking paste service");
            await _pasteService.PasteToActiveWindowAsync();
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
            PartialText = string.Empty;
            _sessionTextContext = ForegroundWindowInfo.GetTextContext();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => _pipeline.StartSessionAsync(_sessionTextContext, _cts.Token));
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
    private async Task OpenSettings()
    {
        try
        {
            var settingsVm = new SettingsViewModel(_deviceClient, _appSettings.Audio.DeviceId);
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
                    _deviceClient.SetActiveDevice(newDeviceId);

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
        _pipeline.LocalCommandExecuted -= OnLocalCommandExecuted;

        // Unsubscribe hotkey events
        _hotkeyService.PushToTalkPressed -= OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased -= OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed -= OnPasteHotkeyPressed;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
