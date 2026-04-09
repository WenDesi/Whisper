using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.Core.Models;
using WhisperDesk.Core.Providers.Stt;

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

    private PipelineState _state = PipelineState.Idle;
    private SessionContextBuilder? _contextBuilder;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

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

    public StreamingPipeline(
        ILogger<StreamingPipeline> logger,
        PipelineConfig config,
        AudioRouter audioRouter,
        IStreamingSttProvider sttProvider,
        IEnumerable<IContextProvider> contextProviders,
        IEnumerable<IPostProcessingStage> postProcessingStages)
    {
        _logger = logger;
        _config = config;
        _audioRouter = audioRouter;
        _sttProvider = sttProvider;
        _contextProviders = contextProviders;
        _postProcessingStages = postProcessingStages.OrderBy(s => s.Order).ToList();
    }

    public async Task StartSessionAsync(CancellationToken ct = default)
    {
        using var activity = DiagnosticSources.Pipeline.StartActivity("Pipeline.StartSession");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        if (!await _sessionLock.WaitAsync(0, ct))
        {
            _logger.LogWarning("[Pipeline] Session start already in progress.");
            activity?.SetTag("result", "already_in_progress");
            return;
        }

        try
        {
            if (State != PipelineState.Idle && State != PipelineState.Completed && State != PipelineState.Error)
            {
                _logger.LogWarning("[Pipeline] Cannot start session in state {State}.", State);
                activity?.SetTag("result", "invalid_state");
                activity?.SetTag("pipeline.state", State.ToString());
                return;
            }

            try
            {
                _logger.LogInformation("[Pipeline] Starting session...");
                State = PipelineState.Listening;

                var audioFormat = new AudioFormat
                {
                    SampleRate = _config.Audio.SampleRate,
                    Channels = _config.Audio.Channels,
                    BitsPerSample = _config.Audio.BitsPerSample
                };

                // 1. Start mic capture IMMEDIATELY (buffered)
                using (var audioStep = DiagnosticSources.Pipeline.StartActivity("Pipeline.StartSession.AudioStart"))
                {
                    audioStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                    _audioRouter.Start(audioFormat);
                }

                // 2. Prepare context + start STT in parallel
                _contextBuilder = new SessionContextBuilder();
                _contextBuilder.AddLanguages(_config.AutoDetectLanguages);

                // Run context providers in parallel (non-blocking)
                using (var contextStep = DiagnosticSources.Pipeline.StartActivity("Pipeline.StartSession.PrepareContext"))
                {
                    contextStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                    await PrepareContextAsync(_contextBuilder, ct);
                }

                // 3. Build STT session options with collected context
                var sttOptions = new SttSessionOptions
                {
                    AudioFormat = audioFormat,
                    Languages = _contextBuilder.Languages,
                    PhraseHints = _contextBuilder.PhraseHints
                };

                // Wire partial results
                _sttProvider.PartialResultReceived += OnPartialResult;
                _sttProvider.ErrorOccurred += OnSttError;

                // Start STT session
                using (var sttStep = DiagnosticSources.Pipeline.StartActivity("Pipeline.StartSession.SttStart"))
                {
                    sttStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                    sttStep?.SetTag("stt.provider", _sttProvider.Name);
                    sttStep?.SetTag("stt.languages", string.Join(",", sttOptions.Languages));
                    sttStep?.SetTag("stt.phrase_hints.count", sttOptions.PhraseHints.Count);
                    await _sttProvider.StartSessionAsync(sttOptions, ct);
                }

                // 4. Connect audio router to STT provider (flushes buffered audio)
                using (var sinkStep = DiagnosticSources.Pipeline.StartActivity("Pipeline.StartSession.SetSink"))
                {
                    sinkStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                    _audioRouter.SetSink(_sttProvider.PushAudio);
                }

                _logger.LogInformation("[Pipeline] Session started. Streaming audio to STT.");
                activity?.SetTag("result", "success");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Pipeline] Failed to start session.");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddTag("exception.type", ex.GetType().FullName);
                activity?.AddTag("exception.message", ex.Message);
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
        using var activity = DiagnosticSources.Pipeline.StartActivity("Pipeline.StopSession");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        if (State != PipelineState.Listening)
        {
            _logger.LogWarning("[Pipeline] Cannot stop session in state {State}.", State);
            activity?.SetTag("result", "invalid_state");
            activity?.SetTag("pipeline.state", State.ToString());
            return null;
        }

        try
        {
            _logger.LogInformation("[Pipeline] Stopping session...");

            // 1. Stop mic capture
            using (var audioStop = DiagnosticSources.Pipeline.StartActivity("Pipeline.StopSession.AudioStop"))
            {
                audioStop?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                _audioRouter.Stop();
            }

            // 2. Signal end of audio to STT
            State = PipelineState.Transcribing;
            using (var signalStep = DiagnosticSources.Pipeline.StartActivity("Pipeline.StopSession.SignalEndOfAudio"))
            {
                signalStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                _sttProvider.SignalEndOfAudio();
            }

            // 3. Get final transcript
            string rawTranscript;
            using (var endSttStep = DiagnosticSources.Pipeline.StartActivity("Pipeline.StopSession.EndSttSession"))
            {
                endSttStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                rawTranscript = await _sttProvider.EndSessionAsync();
                endSttStep?.SetTag("transcript.length", rawTranscript?.Length ?? 0);
            }

            // Unhook events
            _sttProvider.PartialResultReceived -= OnPartialResult;
            _sttProvider.ErrorOccurred -= OnSttError;

            if (string.IsNullOrWhiteSpace(rawTranscript))
            {
                _logger.LogWarning("[Pipeline] STT returned empty transcript.");
                activity?.SetTag("result", "empty_transcript");
                State = PipelineState.Idle;
                return null;
            }

            _logger.LogInformation("[Pipeline] Raw transcript ({Length} chars): {Text}",
                rawTranscript.Length, rawTranscript);

            // 4. Run post-processing stages
            State = PipelineState.PostProcessing;
            string processedText;
            using (var postStep = DiagnosticSources.Pipeline.StartActivity("Pipeline.StopSession.PostProcessing"))
            {
                postStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                postStep?.SetTag("input.length", rawTranscript.Length);
                processedText = await RunPostProcessingAsync(rawTranscript, ct);
                postStep?.SetTag("output.length", processedText.Length);
            }

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

            activity?.SetTag("result", "success");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline] Failed to stop session.");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
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
        using var activity = DiagnosticSources.Pipeline.StartActivity("Pipeline.AbortSession");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

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
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
        }

        State = PipelineState.Idle;
    }

    public byte[]? GetRecordingAsWav() => _audioRouter.GetRecordingAsWav();

    private async Task PrepareContextAsync(SessionContextBuilder builder, CancellationToken ct)
    {
        using var activity = DiagnosticSources.Pipeline.StartActivity("Pipeline.PrepareContext");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("provider.count", _contextProviders.Count());

        var tasks = _contextProviders.Select(async provider =>
        {
            try
            {
                _logger.LogDebug("[Pipeline] Running context provider: {Name}", provider.Name);
                await provider.ContributeAsync(builder, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Pipeline] Context provider '{Name}' failed (non-fatal).", provider.Name);
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<string> RunPostProcessingAsync(string text, CancellationToken ct)
    {
        using var activity = DiagnosticSources.Pipeline.StartActivity("Pipeline.RunPostProcessing");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("stage.count", _postProcessingStages.Count);
        activity?.SetTag("input.length", text.Length);

        var context = new PostProcessingContext
        {
            RawTranscript = text,
            Language = _config.Language,
            PhraseHints = _contextBuilder?.PhraseHints ?? [],
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

        activity?.SetTag("output.length", current.Length);
        return current;
    }

    private void OnPartialResult(object? sender, SttPartialResult result)
    {
        PartialTranscriptUpdated?.Invoke(this, result.Text);
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
