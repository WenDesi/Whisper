using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Contract;
using WhisperDesk.Stt.Contract;
using WhisperDesk.Core.Services;
using WhisperDesk.Llm.Contract;
using WhisperDesk.Transcript.Contract;
using Fluid;

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
    private readonly ILlmProvider _llmProvider;

    private static readonly FluidParser _fluidParser = new();
    private static readonly Lazy<IFluidTemplate> _correctionTemplate = new(LoadCorrectionTemplate);

    private PipelineState _state = PipelineState.Idle;
    private SessionContextBuilder? _contextBuilder;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private string _foregroundProcess = "";
    private string _foregroundWindowTitle = "";

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
        AudioDeviceService audioDeviceService,
        ITranscriptionHistoryService historyService,
        ILlmProvider llmProvider,
        IEnumerable<IContextProvider> contextProviders,
        IEnumerable<IPostProcessingStage> postProcessingStages)
    {
        _logger = logger;
        _config = config;
        _audioRouter = audioRouter;
        _sttProvider = sttProvider;
        _audioDeviceService = audioDeviceService;
        _historyService = historyService;
        _llmProvider = llmProvider;
        _contextProviders = contextProviders;
        _postProcessingStages = postProcessingStages.OrderBy(s => s.Order).ToList();
    }

    public async Task StartSessionAsync(string foregroundProcess = "", string foregroundWindowTitle = "", CancellationToken ct = default)
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
                _foregroundProcess = foregroundProcess;
                _foregroundWindowTitle = foregroundWindowTitle;

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
                ForegroundProcess = _foregroundProcess,
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

    public async Task<string?> CorrectLastResultAsync(string previousText, string correctionTranscript, CancellationToken ct = default)
    {
        _logger.LogInformation("[Pipeline] CorrectLastResultAsync called. CorrectionTranscript={CorrectionLen} chars, PreviousText={PrevLen} chars",
            correctionTranscript.Length, previousText.Length);
        _logger.LogDebug("[Pipeline] Correction input — Previous: {Previous}", previousText);
        _logger.LogDebug("[Pipeline] Correction input — UserSaid: {UserSaid}", correctionTranscript);

        if (string.IsNullOrEmpty(previousText))
        {
            _logger.LogWarning("[Pipeline] No previous text to correct.");
            return null;
        }

        try
        {
            var template = _correctionTemplate.Value;
            var templateContext = new TemplateContext();
            templateContext.SetValue("previous_text", previousText);
            templateContext.SetValue("correction_text", correctionTranscript);

            var userPrompt = await template.RenderAsync(templateContext);
            _logger.LogDebug("[Pipeline] Correction prompt rendered ({Length} chars).", userPrompt.Length);

            var corrected = await _llmProvider.ProcessTextAsync(
                "You are a transcription correction assistant. Apply the user's spoken correction to the previous transcription.",
                userPrompt,
                new LlmRequestOptions { Temperature = 0.2f },
                ct);

            _logger.LogInformation("[Pipeline] Correction complete. Previous={PrevLen} chars -> Corrected={CorrLen} chars.",
                previousText.Length, corrected.Length);
            _logger.LogDebug("[Pipeline] Correction result: {Corrected}", corrected);

            LastProcessedText = corrected;
            return corrected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline] Correction failed.");
            return null;
        }
    }

    private static IFluidTemplate LoadCorrectionTemplate()
    {
        var assembly = typeof(StreamingPipeline).Assembly;
        var resourceName = "WhisperDesk.Core.Stages.PostProcessing.Prompts.Correction.liquid";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var templateText = reader.ReadToEnd();

        if (!_fluidParser.TryParse(templateText, out var template, out var error))
        {
            throw new InvalidOperationException($"Failed to parse Correction.liquid: {error}");
        }
        return template;
    }

    public void Dispose()
    {
        _audioRouter.Dispose();
        _sttProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
