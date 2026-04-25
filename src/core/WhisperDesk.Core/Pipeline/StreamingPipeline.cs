using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Contract;
using WhisperDesk.Stt.Contract;
using WhisperDesk.Core.Services;
using WhisperDesk.Transcript.Contract;
using System.Collections.Concurrent;
using WhisperDesk.Llm.Contract;

namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// Main pipeline orchestrator. Coordinates:
/// 1. Context preparation (parallel with audio startup)
/// 2. Audio capture via AudioRouter
/// 3. STT via pluggable IStreamingSttProvider
/// 4. Post-processing via ordered IPostProcessingStage chain
/// </summary>
public class StreamingPipeline : IPipelineController, IDisposable
{
    private readonly ILogger<StreamingPipeline> _logger;
    private readonly PipelineConfig _config;
    private readonly AudioRouter _audioRouter;
    private readonly IStreamingSttProvider _sttProvider;
    private readonly IEnumerable<IContextProvider> _contextProviders;
    private readonly IReadOnlyList<IPostProcessingStage> _postProcessingStages;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly ITranscriptionHistoryService _historyService;

    private PipelineState _state = PipelineState.Idle;
    private SessionContextBuilder? _contextBuilder;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private string _foregroundWindowTitle = "";
    private WindowTextSerializationInfo? _sessionTextContext= null;
    private readonly int _timeoutMs = 10000; // Timeout for waiting on command responses

    public PipelineState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public string? LastProcessedText { get; private set; }
    public bool HasRecordingData => _audioRouter.HasRecordingData;

    public event EventHandler<PipelineState>? StateChanged;
    public event EventHandler<string>? PartialTranscriptUpdated;
    public event EventHandler<PipelineResult>? SessionCompleted;
    public event EventHandler<PipelineError>? ErrorOccurred;
    public event EventHandler<CommandEvent>? LocalCommandExecuted;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _commandResponseSources = new();

    public StreamingPipeline(
        ILogger<StreamingPipeline> logger,
        PipelineConfig config,
        AudioRouter audioRouter,
        IStreamingSttProvider sttProvider,
        AudioDeviceService audioDeviceService,
        ITranscriptionHistoryService historyService,
        IEnumerable<IContextProvider> contextProviders,
        IEnumerable<IPostProcessingStage> postProcessingStages)
    {
        _logger = logger;
        _config = config;
        _audioRouter = audioRouter;
        _sttProvider = sttProvider;
        _audioDeviceService = audioDeviceService;
        _historyService = historyService;
        _contextProviders = contextProviders;
        _postProcessingStages = postProcessingStages.OrderBy(s => s.Order).ToList();
    }

    public async Task StartSessionAsync(WindowTextSerializationInfo? textContext=null, CancellationToken ct = default)
    {
        if (!await _sessionLock.WaitAsync(0, ct))
        {
            _logger.LogWarning("[Pipeline] Session start already in progress.");
            return;
        }

        try
        {
            if (State != PipelineState.Idle && State != PipelineState.Completed && State != PipelineState.Error)
            {
                _logger.LogWarning("[Pipeline] Cannot start session in state {State}.", State);
                return;
            }

            try
            {
                _logger.LogInformation("[Pipeline] Starting session...");
                State = PipelineState.Listening;
                _sessionTextContext = textContext;
                _foregroundWindowTitle = textContext?.MainWindowTitle ?? "";

                var audioFormat = new AudioFormat
                {
                    SampleRate = _config.Audio.SampleRate,
                    Channels = _config.Audio.Channels,
                    BitsPerSample = _config.Audio.BitsPerSample
                };

                // 1. Start mic capture IMMEDIATELY (buffered)
                var deviceNumber = _audioDeviceService.ResolveWaveInDeviceNumber(_config.AudioDeviceId);
                _audioRouter.Start(audioFormat, deviceNumber);

                // 2. Prepare context + start STT in parallel
                _contextBuilder = new SessionContextBuilder();
                _contextBuilder.AddLanguages(_config.AutoDetectLanguages);
                _contextBuilder.SetMetadata("foregroundProcess", _foregroundProcess);
                _contextBuilder.SetMetadata("foregroundWindowTitle", _foregroundWindowTitle);

                // Run context providers sequentially by Order (non-blocking)
                await PrepareContextAsync(_contextBuilder, ct);

                // 3. Build STT session options with collected context
                var sttOptions = new SttSessionOptions
                {
                    AudioFormat = audioFormat,
                    Languages = _contextBuilder.Languages,
                    PhraseHints = _contextBuilder.PhraseHints,
                    DialogContext = _contextBuilder.DialogTurns
                };

                // Wire partial results
                _sttProvider.PartialResultReceived += OnPartialResult;
                _sttProvider.ErrorOccurred += OnSttError;

                // Start STT session
                await _sttProvider.StartSessionAsync(sttOptions, ct);

                // 4. Connect audio router to STT provider (flushes buffered audio)
                _audioRouter.SetSink(_sttProvider.PushAudio);

                _logger.LogInformation("[Pipeline] Session started. Streaming audio to STT.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Pipeline] Failed to start session.");
                State = PipelineState.Error;
                ErrorOccurred?.Invoke(this, new PipelineError
                {
                    Stage = "StartSession",
                    Message = $"Failed to start session: {ex.Message}",
                    Exception = ex
                });
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<PipelineResult?> StopSessionAsync(CancellationToken ct = default)
    {
        if (State != PipelineState.Listening)
        {
            _logger.LogWarning("[Pipeline] Cannot stop session in state {State}.", State);
            return null;
        }

        try
        {
            _logger.LogInformation("[Pipeline] Stopping session...");

            // 1. Stop mic capture
            _audioRouter.Stop();

            // 2. Signal end of audio to STT
            State = PipelineState.Transcribing;
            _sttProvider.SignalEndOfAudio();

            // 3. Get final transcript
            var rawTranscript = await _sttProvider.EndSessionAsync();

            // Unhook events
            _sttProvider.PartialResultReceived -= OnPartialResult;
            _sttProvider.ErrorOccurred -= OnSttError;

            if (string.IsNullOrWhiteSpace(rawTranscript))
            {
                _logger.LogWarning("[Pipeline] STT returned empty transcript.");
                State = PipelineState.Idle;
                return null;
            }

            _logger.LogInformation("[Pipeline] Raw transcript ({Length} chars): {Text}",
                rawTranscript.Length, rawTranscript);

            // 4. Run post-processing stages
            State = PipelineState.PostProcessing;
            var processedText = await RunPostProcessingAsync(rawTranscript, ct);

            _logger.LogInformation("[Pipeline] Processed text ({Length} chars): {Text}",
                processedText.Length, processedText);

            // 5. Build result
            LastProcessedText = processedText;
            var result = new PipelineResult
            {
                RawTranscript = rawTranscript,
                ProcessedText = processedText,
                Language = _config.Language
            };

            State = PipelineState.Completed;
            SessionCompleted?.Invoke(this, result);

            var entry = new TranscriptionHistoryEntry
            {
                Id = result.Id,
                Timestamp = result.Timestamp,
                Duration = result.AudioDuration,
                Language = result.Language,
                RawText = result.RawTranscript,
                ProcessedText = result.ProcessedText,
                Source = result.SourceFile ?? "microphone",
                SttProvider = _config.SttProvider,
                LlmProvider = _config.LlmProvider,
                ForegroundProcess = "",
                ForegroundWindowTitle = _foregroundWindowTitle
            };
            _ = _historyService.WriteEntryAsync(entry);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline] Failed to stop session.");
            State = PipelineState.Error;
            ErrorOccurred?.Invoke(this, new PipelineError
            {
                Stage = "StopSession",
                Message = $"Failed to process recording: {ex.Message}",
                Exception = ex
            });
            return null;
        }
    }

    public async Task AbortSessionAsync()
    {
        _logger.LogInformation("[Pipeline] Aborting session...");

        _audioRouter.Stop();

        try
        {
            _sttProvider.PartialResultReceived -= OnPartialResult;
            _sttProvider.ErrorOccurred -= OnSttError;
            _sttProvider.SignalEndOfAudio();
            await _sttProvider.EndSessionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pipeline] Error during abort cleanup.");
        }

        State = PipelineState.Idle;
    }

    public async Task RetrySessionAsync(WindowTextContext? textContext = null, CancellationToken ct = default)
    {
        await AbortSessionAsync();
        await StartSessionAsync(textContext, ct);
    }
    public byte[]? GetRecordingAsWav() => _audioRouter.GetRecordingAsWav();

    private async Task PrepareContextAsync(SessionContextBuilder builder, CancellationToken ct)
    {
        foreach (var provider in _contextProviders.OrderBy(p => p.Order))
        {
            try
            {
                _logger.LogDebug("[Pipeline] Running context provider: {Name} (order={Order})", provider.Name, provider.Order);
                await provider.ContributeAsync(builder, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Pipeline] Context provider '{Name}' failed (non-fatal).", provider.Name);
            }
        }
    }

    private async Task<string> RunPostProcessingAsync(string text, CancellationToken ct)
    {
        var context = new PostProcessingContext
        {
            RawTranscript = text,
            Language = _config.Language,
            PhraseHints = _contextBuilder?.PhraseHints ?? [],
            ToolContext = BuildToolContext(ct),
        };

        var current = text;
        foreach (var stage in _postProcessingStages)
        {
            try
            {
                _logger.LogDebug("[Pipeline] Running post-processing stage: {Name} (order={Order})",
                    stage.Name, stage.Order);
                current = await stage.ProcessAsync(current, context, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Pipeline] Post-processing stage '{Name}' failed.", stage.Name);
                // Continue with current text — don't fail the whole pipeline
            }
        }

        return current;
    }

    private void OnPartialResult(object? sender, SttPartialResult result)
    {
        PartialTranscriptUpdated?.Invoke(this, result.Text);
    }

    public void SendCommandResult(CommandResult commandResult)
    {
        _logger.LogInformation("[Pipeline] SendCommandResult called: CommandId={CommandId}, ResultType={ResultType}",
            commandResult.CommandId, commandResult.Result.GetType().Name);

        if (_commandResponseSources.TryGetValue(commandResult.CommandId, out var tcs))
        {
            var set = tcs.TrySetResult(commandResult);
            _logger.LogInformation("[Pipeline] TCS.TrySetResult for CommandId={CommandId}: {Set}", commandResult.CommandId, set);
        }
        else
        {
            _logger.LogWarning("[Pipeline] No pending command found for response with CommandId '{CommandId}'. Pending: [{Pending}]",
                commandResult.CommandId, string.Join(", ", _commandResponseSources.Keys));
        }
    }

    private ToolContext BuildToolContext(CancellationToken ct)
    {
        var hasSelectedText = !string.IsNullOrEmpty(_sessionTextContext?.Selected);

        var tools = new List<ToolDefinition>
        {
            new ToolDefinition
            {
                Name = "append",
                Description = "Append new content to the end of the current input field or editor. Use this tool when the user's intent is to ADD new content rather than modify existing text — for example, continuing a sentence, inserting a follow-up paragraph, or applying an LLM-generated addition back to the context.",
                ParametersSchema = """{"type":"object","properties":{"content":{"type":"string","description":"The text to append"}},"required":["content"]}"""
            },
            new ToolDefinition
            {
                Name = "replace",
                Description = "Replace a specific portion of the current input field or editor with new content. Use this tool when the user's intent is to MODIFY or rewrite existing text — for example, correcting a sentence, rephrasing a paragraph, or applying an LLM-generated edit back to the original context.",
                ParametersSchema = """{"type":"object","properties":{"originalText":{"type":"string","description":"The exact text to replace"},"targetText":{"type":"string","description":"The replacement text"}},"required":["originalText","targetText"]}"""
            },
        };

        // Only expose `read_all_context` when the user has NOT pre-selected text.
        // When a selection exists, that selection IS the context — no need to scan the whole document.
        if (!hasSelectedText)
        {
            tools.Add(new ToolDefinition
            {
                Name = "read_all_context",
                Description = "Read the full text of the current editor, web page, or document. AVOID calling this tool unless absolutely necessary — it has SIDE EFFECTS: in some applications it must briefly take focus, send Ctrl+A/Ctrl+C, and may move the caret or flash the clipboard. Only call it when the user's instruction CANNOT be fulfilled from the transcript alone (e.g., 'summarize this page', 'answer based on the document'). For local edits like 'fix the grammar of what I said' or 'rephrase this sentence', do NOT call it. Returns the complete text as a string.",
                ParametersSchema = """{"type":"object","properties":{}}"""
            });
        }

        return new ToolContext
        {
            Tools = tools,
            ToolExecutor = async (toolName, arguments, toolCt) =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, toolCt);
                _logger.LogInformation("[Pipeline] Tool execution requested: {ToolName} with arguments {Arguments}", toolName, arguments);
                var command = toolName switch
                {
                    "append" => new CommandEvent
                    {
                        CommandType = CommandType.Append,
                        Payload = new AppendCommandPayload
                        {
                            Content = ParseRequiredString(arguments, "content")
                        }
                    },
                    "replace" => new CommandEvent
                    {
                        CommandType = CommandType.Replace,
                        Payload = new ReplaceCommandPayload
                        {
                            OriginalText = ParseRequiredString(arguments, "originalText"),
                            TargetText = ParseRequiredString(arguments, "targetText")
                        }
                    },
                    "read_all_context" => new CommandEvent
                    {
                        CommandType = CommandType.ReadAllContext,
                        Payload = new ReadAllContextCommandPayload()
                    },
                    _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
                };
                return await ExecuteRemoteCommandAsync(null, command, cts.Token);
            },
            SelectedText = _sessionTextContext?.Selected ?? "",
            MainWindowTitle = _sessionTextContext?.MainWindowTitle ?? ""
        };
    }

    private static string ParseRequiredString(string json, string key)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(key).GetString()
            ?? throw new ArgumentException($"Tool argument '{key}' is null.");
    }

    private async Task<string> ExecuteRemoteCommandAsync(object? sender, CommandEvent command, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commandResponseSources[command.CommandId] = tcs;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
        try
        {
            ct.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

            var subscriberCount = LocalCommandExecuted?.GetInvocationList().Length ?? 0;
            _logger.LogInformation("[Pipeline] Firing LocalCommandExecuted: CommandId={CommandId}, Type={Type}, Subscribers={Count}",
                command.CommandId, command.CommandType, subscriberCount);

            LocalCommandExecuted?.Invoke(this, command);

            _logger.LogInformation("[Pipeline] Waiting for command result: CommandId={CommandId}, TimeoutMs={TimeoutMs}",
                command.CommandId, _timeoutMs);

            var result = await tcs.Task;

            _logger.LogInformation("[Pipeline] Command result received: CommandId={CommandId}, ResultType={ResultType}",
                command.CommandId, result.Result.GetType().Name);

            return result.Result switch
            {
                TextCommandResult textResult => textResult.Result,
                _ => throw new InvalidOperationException("Unsupported command result type")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("[Pipeline] Command '{CommandId}' timed out after {TimeoutMs} ms.", command.CommandId, _timeoutMs);
            throw new TimeoutException($"Command timed out after {_timeoutMs} ms.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Pipeline] Command '{CommandId}' cancelled by caller.", command.CommandId);
            throw;
        }
        finally
        {
            _commandResponseSources.TryRemove(command.CommandId, out _);
        }
    }

    private void OnSttError(object? sender, SttError error)
    {
        _logger.LogError("[Pipeline] STT error: {Code} — {Message}", error.Code, error.Message);
        ErrorOccurred?.Invoke(this, new PipelineError
        {
            Stage = "STT",
            Message = error.Message,
            Exception = error.Exception
        });
    }

    public void Dispose()
    {
        _audioRouter.Dispose();
        _sttProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
