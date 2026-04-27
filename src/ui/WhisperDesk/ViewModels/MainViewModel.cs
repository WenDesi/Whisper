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
    private bool _isCorrectionMode;

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
    public string CorrectionHint => $"✏️ Hold {_appSettings.Hotkeys.CorrectionHotkey} to correct";

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

        // Wire hotkey events
        _hotkeyService.PushToTalkPressed += OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased += OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed += OnPasteHotkeyPressed;
        _hotkeyService.CorrectionPressed += OnCorrectionPressed;
        _hotkeyService.CorrectionReleased += OnCorrectionReleased;

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

            if (string.IsNullOrEmpty(result.ProcessedText))
                return;

            if (_isCorrectionMode)
            {
                _logger.LogInformation("[ViewModel] Correction mode: sending correction transcript to LLM. Transcript={Len} chars",
                    result.ProcessedText.Length);
                _isCorrectionMode = false;
                var correctionTranscript = result.ProcessedText;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogDebug("[ViewModel] Calling CorrectLastResultAsync...");
                        var corrected = await _pipeline.CorrectLastResultAsync(correctionTranscript);

                        if (string.IsNullOrEmpty(corrected))
                        {
                            _logger.LogWarning("[ViewModel] Correction returned null/empty. No action taken.");
                            return;
                        }

                        _logger.LogInformation("[ViewModel] Correction result: {Len} chars", corrected.Length);

                        // Update UI
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            CleanedText = corrected;
                        });

                        // Write corrected text to clipboard
                        bool clipboardOk = false;
                        var staThread = new Thread(() =>
                        {
                            for (int i = 0; i < 5; i++)
                            {
                                try
                                {
                                    Clipboard.SetDataObject(corrected, true);
                                    clipboardOk = true;
                                    _logger.LogInformation("[ViewModel] Corrected text copied to clipboard.");
                                    break;
                                }
                                catch (System.Runtime.InteropServices.COMException ex)
                                {
                                    _logger.LogWarning("[ViewModel] Clipboard busy (attempt {Attempt}/5): {Message}", i + 1, ex.Message);
                                    if (i < 4) Thread.Sleep(100);
                                }
                            }
                        })
                        {
                            IsBackground = true
                        };
                        staThread.SetApartmentState(ApartmentState.STA);
                        staThread.Start();
                        staThread.Join();

                        if (clipboardOk)
                        {
                            await Task.Delay(500);
                            Application.Current?.Dispatcher.InvokeAsync(async () =>
                            {
                                _logger.LogDebug("[ViewModel] Performing undo + paste for correction.");
                                await _pasteService.UndoAndPasteAsync();
                            });
                        }
                        else
                        {
                            _logger.LogWarning("[ViewModel] Clipboard unavailable after retries, skipping correction paste.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[ViewModel] Correction mode failed.");
                    }
                });
            }
            else
            {
                // Normal mode: write to clipboard and paste
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
                    })
                    {
                        IsBackground = true
                    };
                    staThread.SetApartmentState(ApartmentState.STA);
                    staThread.Start();
                    staThread.Join();

                    if (clipboardOk)
                    {
                        // Wait for RDP clipboard sync before pasting
                        await Task.Delay(500);
                        Application.Current?.Dispatcher.InvokeAsync(async () =>
                        {
                            await _pasteService.PasteToActiveWindowAsync();
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
                var (proc, title) = ForegroundWindowInfo.Get();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => _pipeline.StartSessionAsync(proc, title, _cts.Token));
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

    private void OnCorrectionPressed(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Status == AppStatus.Idle || Status == AppStatus.Ready || Status == AppStatus.Error)
            {
                if (string.IsNullOrEmpty(_pipeline.LastProcessedText))
                {
                    _logger.LogWarning("[ViewModel] Correction hotkey pressed but no previous result to correct.");
                    return;
                }

                _logger.LogInformation("[ViewModel] Correction mode activated. Previous text: {Len} chars", _pipeline.LastProcessedText.Length);
                _isCorrectionMode = true;
                PartialText = string.Empty;
                var (proc, title) = ForegroundWindowInfo.Get();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => _pipeline.StartSessionAsync(proc, title, _cts.Token));
            }
        });
    }

    private void OnCorrectionReleased(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Status == AppStatus.Listening && !_isStopping)
            {
                _logger.LogDebug("[ViewModel] Correction hotkey released, stopping session.");
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
            var (proc, title) = ForegroundWindowInfo.Get();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => _pipeline.StartSessionAsync(proc, title, _cts.Token));
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

        // Unsubscribe hotkey events
        _hotkeyService.PushToTalkPressed -= OnPushToTalkPressed;
        _hotkeyService.PushToTalkReleased -= OnPushToTalkReleased;
        _hotkeyService.PasteHotkeyPressed -= OnPasteHotkeyPressed;
        _hotkeyService.CorrectionPressed -= OnCorrectionPressed;
        _hotkeyService.CorrectionReleased -= OnCorrectionReleased;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
