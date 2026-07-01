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
using WhisperDesk.Telemetry;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly IPipelineController _pipeline;
    private readonly HotkeyService _hotkeyService;
    private readonly GrpcDeviceClient _deviceClient;
    private readonly WhisperDeskSettings _appSettings;
    private readonly TextInjectionService _textInjection;
    private CancellationTokenSource? _cts;
    private bool _isStopping;
    private WindowTextContext? _sessionTextContext;
    private readonly object _draftLock = new();
    private PendingDraft? _pendingDraft;
    private CancellationTokenSource? _draftCommitCts;
    private System.Diagnostics.Activity? _recordingSessionActivity;
    private SessionMode _activeSessionMode = SessionMode.Transcribe;

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

    public string PushToTalkHint => $"\U0001f3a4 Hold {_appSettings.Hotkeys.Transcribe} to dictate, {_appSettings.Hotkeys.Instruct} to instruct";

    public event EventHandler<DraftPreview>? DraftPreviewChanged;
    public event EventHandler? DraftPreviewClosed;

    public sealed record DraftPreview(string Text, TimeSpan CommitDelay);

    private sealed record PendingDraft(string Text, WindowTextContext Context, TimeSpan CommitDelay);

    public MainViewModel(
        ILogger<MainViewModel> logger,
        IPipelineController pipeline,
        HotkeyService hotkeyService,
        GrpcDeviceClient deviceClient,
        WhisperDeskSettings appSettings,
        TextInjectionService textInjection)
    {
        _logger = logger;
        _pipeline = pipeline;
        _hotkeyService = hotkeyService;
        _deviceClient = deviceClient;
        _appSettings = appSettings;
        _textInjection = textInjection;

        // Wire pipeline events
        _pipeline.StateChanged += OnPipelineStateChanged;
        _pipeline.SessionCompleted += OnSessionCompleted;
        _pipeline.ErrorOccurred += OnPipelineError;
        _pipeline.PartialTranscriptUpdated += OnPartialTranscript;
        _pipeline.LocalCommandExecuted += OnLocalCommandExecuted;

        // Wire hotkey events
        _hotkeyService.RecordPressed += OnRecordPressed;
        _hotkeyService.RecordReleased += OnRecordReleased;

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
        _recordingSessionActivity?.SetTag("session.result", "completed");
        _recordingSessionActivity?.Dispose();
        _recordingSessionActivity = null;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            RawText = result.RawTranscript;
            CleanedText = result.ProcessedText;
            PartialText = string.Empty;

            _logger.LogInformation("[ViewModel] Session completed (mode={Mode}). Raw: {RawLen} chars, Cleaned: {CleanLen} chars.",
                result.Mode, RawText.Length, CleanedText.Length);
        });

        if (result.Mode == SessionMode.Transcribe && !string.IsNullOrEmpty(result.ProcessedText))
        {
            QueueTranscriptionDraft(result.ProcessedText);
        }
        else if (result.Mode == SessionMode.Instruct && !string.IsNullOrWhiteSpace(result.ProcessedText))
        {
            UpdatePendingDraft(result.ProcessedText);
        }
        else if (result.Mode == SessionMode.Instruct)
        {
            ReschedulePendingDraftCommitIfNeeded();
        }
    }

    private void OnPipelineError(object? sender, PipelineError error)
    {
        _recordingSessionActivity?.SetTag("session.result", "error");
        _recordingSessionActivity?.SetTag("error.stage", error.Stage);
        _recordingSessionActivity?.Dispose();
        _recordingSessionActivity = null;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            LastError = error.Message;
            HasError = true;
        });
        ReschedulePendingDraftCommitIfNeeded();
    }

    private void OnPartialTranscript(object? sender, string partialText)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            PartialText = partialText;
        });
    }

    private void OnRecordPressed(object? sender, SessionMode pressMode)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Status == AppStatus.Idle || Status == AppStatus.Ready || Status == AppStatus.Error)
            {
                var effectiveMode = pressMode == SessionMode.Transcribe && HasPendingDraft()
                    ? SessionMode.Instruct
                    : pressMode;
                BeginSession(effectiveMode);
            }
        });
    }

    public void BeginPushToTalk() => OnRecordPressed(this, SessionMode.Transcribe);
    public void EndPushToTalk() => OnRecordReleased(this, SessionMode.Transcribe);

    private void OnRecordReleased(object? sender, SessionMode releaseMode)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Status == AppStatus.Listening && !_isStopping)
            {
                _isStopping = true;
                _ = Task.Run(async () =>
                {
                    PipelineResult? result = null;
                    var sessionMode = _activeSessionMode;
                    try
                    {
                        result = await _pipeline.StopSessionAsync(sessionMode, _cts?.Token ?? CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ViewModel] Failed to stop session.");
                    }
                    finally
                    {
                        if (sessionMode == SessionMode.Instruct && result is null)
                        {
                            ReschedulePendingDraftCommitIfNeeded();
                        }
                        if (result is null)
                        {
                            _recordingSessionActivity?.SetTag("session.result", "empty");
                            _recordingSessionActivity?.Dispose();
                            _recordingSessionActivity = null;
                        }
                        Application.Current?.Dispatcher.InvokeAsync(() => _isStopping = false);
                    }
                });
            }
        });
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
                    var result = await _pipeline.StopSessionAsync(_activeSessionMode, _cts?.Token ?? CancellationToken.None);
                    if (result is null)
                    {
                        _recordingSessionActivity?.SetTag("session.result", "empty");
                        _recordingSessionActivity?.Dispose();
                        _recordingSessionActivity = null;
                    }
                }
                finally
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => _isStopping = false);
                }
            });
        }
        else
        {
            BeginSession(SessionMode.Transcribe);
        }
    }

    private void BeginSession(SessionMode mode)
    {
        _recordingSessionActivity?.Dispose();
        _recordingSessionActivity = WhisperDeskTelemetry.StartActivity("recording_session");
        _recordingSessionActivity?.SetTag("session.mode", mode.ToString());

        using var activity = WhisperDeskTelemetry.StartActivity("ui.begin_session");
        activity?.SetTag("session.mode", mode.ToString());
        PartialText = string.Empty;
        _activeSessionMode = mode;
        var pendingDraft = HasPendingDraft();
        activity?.SetTag("draft.pending", pendingDraft);

        if (mode == SessionMode.Instruct && TryBeginDraftCorrection(out var draftContext))
        {
            _sessionTextContext = draftContext;
            activity?.SetTag("context.source", "pending_draft");
        }
        else
        {
            if (mode == SessionMode.Transcribe)
            {
                using (WhisperDeskTelemetry.StartActivity("ui.commit_pending_draft"))
                {
                    CommitPendingDraftNow();
                }
            }
            _sessionTextContext = ForegroundWindowInfo.GetTextContext();
            activity?.SetTag("context.source", _sessionTextContext is null ? "none" : "foreground_window");
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var startSessionActivity = WhisperDeskTelemetry.StartActivity("ui.grpc.pipeline_start_session");
            startSessionActivity?.SetTag("session.mode", mode.ToString());
            await _pipeline.StartSessionAsync(_sessionTextContext, mode, _cts.Token);
        });
    }

    private void QueueTranscriptionDraft(string text)
    {
        var context = _sessionTextContext;
        if (context is null)
        {
            InjectTextAsync(text, context: null);
            return;
        }

        var commitDelay = GetDraftCommitDelay(text);
        lock (_draftLock)
        {
            _pendingDraft = new PendingDraft(text, context, commitDelay);
        }

        _logger.LogInformation("[Draft] Queued transcription draft ({Length} chars) for {DelayMs} ms.",
            text.Length, commitDelay.TotalMilliseconds);
        DraftPreviewChanged?.Invoke(this, new DraftPreview(text, commitDelay));
        SchedulePendingDraftCommit();
    }

    private bool HasPendingDraft()
    {
        lock (_draftLock)
        {
            return _pendingDraft is not null;
        }
    }

    private bool TryBeginDraftCorrection(out WindowTextContext draftContext)
    {
        lock (_draftLock)
        {
            if (_pendingDraft is null)
            {
                draftContext = null!;
                return false;
            }

            CancelDraftCommitTimerCore();
            draftContext = _pendingDraft.Context with
            {
                Selected = string.Empty,
                DraftText = _pendingDraft.Text
            };
            _logger.LogInformation("[Draft] Starting command correction for pending draft ({Length} chars).",
                _pendingDraft.Text.Length);
            return true;
        }
    }

    private void UpdatePendingDraft(string text)
    {
        var commitDelay = GetDraftCommitDelay(text);
        lock (_draftLock)
        {
            if (_pendingDraft is null)
            {
                return;
            }

            _pendingDraft = _pendingDraft with { Text = text, CommitDelay = commitDelay };
            _logger.LogInformation("[Draft] Updated pending draft ({Length} chars).", text.Length);
            DraftPreviewChanged?.Invoke(this, new DraftPreview(text, commitDelay));
        }

        SchedulePendingDraftCommit();
    }

    private void ReschedulePendingDraftCommitIfNeeded()
    {
        lock (_draftLock)
        {
            if (_pendingDraft is null || _draftCommitCts is not null)
            {
                return;
            }
        }

        SchedulePendingDraftCommit();
    }

    private void SchedulePendingDraftCommit()
    {
        var cts = new CancellationTokenSource();
        TimeSpan commitDelay;
        lock (_draftLock)
        {
            CancelDraftCommitTimerCore();
            _draftCommitCts = cts;
            commitDelay = _pendingDraft?.CommitDelay ?? GetDraftCommitDelay(string.Empty);
        }

        _ = CommitDraftAfterDelayAsync(cts, commitDelay);
    }

    private async Task CommitDraftAfterDelayAsync(CancellationTokenSource cts, TimeSpan commitDelay)
    {
        try
        {
            await Task.Delay(commitDelay, cts.Token);
            var draft = TakePendingDraft(cts);
            if (draft is not null)
            {
                DraftPreviewClosed?.Invoke(this, EventArgs.Empty);
                InjectTextAsync(draft.Text, draft.Context);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void CommitPendingDraftNow()
    {
        var draft = TakePendingDraft();
        if (draft is not null)
        {
            DraftPreviewClosed?.Invoke(this, EventArgs.Empty);
            InjectTextAsync(draft.Text, draft.Context);
        }
    }

    private PendingDraft? TakePendingDraft(CancellationTokenSource? expectedCts = null)
    {
        lock (_draftLock)
        {
            if (expectedCts is not null && !ReferenceEquals(_draftCommitCts, expectedCts))
            {
                return null;
            }

            var draft = _pendingDraft;
            _pendingDraft = null;
            CancelDraftCommitTimerCore();
            return draft;
        }
    }

    private void CancelDraftCommitTimerCore()
    {
        var cts = _draftCommitCts;
        _draftCommitCts = null;
        cts?.Cancel();
    }

    private void InjectTextAsync(string text, WindowTextContext? context)
    {
        Task.Run(() =>
        {
            try
            {
                _textInjection.InjectText(text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ViewModel] Text injection failed.");
            }
        });
    }

    private TimeSpan GetDraftCommitDelay(string text)
    {
        const double minimumMs = 1500;
        const double maximumMs = 6000;
        const double lengthScale = 180;

        var length = string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Length;
        var growth = 1 - Math.Exp(-length / lengthScale);

        return TimeSpan.FromMilliseconds(minimumMs + (maximumMs - minimumMs) * growth);
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
        _recordingSessionActivity?.Dispose();
        _recordingSessionActivity = null;

        // Unsubscribe hotkey events
        _hotkeyService.RecordPressed -= OnRecordPressed;
        _hotkeyService.RecordReleased -= OnRecordReleased;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        CommitPendingDraftNow();
        GC.SuppressFinalize(this);
    }
}
