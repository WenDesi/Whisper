using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using WhisperDesk.Core.Contract;
using WhisperDesk.Proto;

namespace WhisperDesk.Server;

public class GrpcPipelineClient : IPipelineController, IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly PipelineService.PipelineServiceClient _client;
    private CancellationTokenSource? _subscribeCts;
    private Task? _subscribeTask;
    private Task? _audioLevelTask;
    private Task? _disposeTask;
    private readonly object _disposeLock = new();
    private int _disposed;
    private float _audioLevel;
    private long _audioLevelTimestamp;
    private volatile PipelineState _state = PipelineState.Idle;

    public PipelineState State => _state;
    public float AudioLevel =>
        State == PipelineState.Listening &&
        Stopwatch.GetElapsedTime(Volatile.Read(ref _audioLevelTimestamp)) < TimeSpan.FromMilliseconds(300)
            ? Volatile.Read(ref _audioLevel)
            : 0;
    public string? LastProcessedText { get; private set; }
    public bool HasRecordingData { get; private set; }

    public event EventHandler<PipelineState>? StateChanged;
    public event EventHandler<string>? PartialTranscriptUpdated;
    public event EventHandler<string>? CleanupChunkProduced;
    public event EventHandler<PipelineResult>? SessionCompleted;
    public event EventHandler<PipelineError>? ErrorOccurred;
    public event EventHandler<CommandEvent>? LocalCommandExecuted;

    public GrpcPipelineClient(string address)
    {
        _channel = GrpcChannel.ForAddress(address);
        _client = new PipelineService.PipelineServiceClient(_channel);
        StartEventSubscription();
    }

    public async Task StartSessionAsync(WindowTextSerializationInfo? textContext = null, SessionMode mode = SessionMode.Transcribe, CancellationToken ct = default)
    {
        await _client.StartSessionAsync(new StartSessionRequest
        {
            ForegroundProcess = "",
            ForegroundWindowTitle = textContext?.MainWindowTitle ?? "",
            FileFullPath = textContext?.FileFullPath ?? "",
            Selected = textContext?.Selected ?? "",
            Mode = MapMode(mode),
            DraftText = textContext?.DraftText ?? ""
        }, cancellationToken: ct);
    }

    public async Task<PipelineResult?> StopSessionAsync(SessionMode? modeOverride = null, CancellationToken ct = default)
    {
        var request = new StopSessionRequest
        {
            Mode = MapMode(modeOverride ?? SessionMode.Transcribe)
        };
        var response = await _client.StopSessionAsync(request, cancellationToken: ct);
        if (response.Result != null)
        {
            var result = MapResult(response.Result);
            LastProcessedText = result.ProcessedText;
            return result;
        }
        return null;
    }

    public async Task AbortSessionAsync()
    {
        await _client.AbortSessionAsync(new AbortSessionRequest());
    }

    public void SendCommandResult(CommandResult commandResult)
    {
        var resultText = commandResult.Result switch
        {
            TextCommandResult t => t.Result,
            _ => ""
        };
        _client.SendCommandResult(new SendCommandResultRequest
        {
            CommandId = commandResult.CommandId,
            ResultText = resultText
        });
    }

    public byte[]? GetRecordingAsWav()
    {
        var response = _client.GetRecordingWav(new GetRecordingWavRequest());
        return response.WavData.IsEmpty ? null : response.WavData.ToByteArray();
    }

    private void StartEventSubscription()
    {
        var subscribeCts = new CancellationTokenSource();
        _subscribeCts = subscribeCts;
        var ct = subscribeCts.Token;
        _subscribeTask = Task.Run(() => ReadSubscriptionAsync(
            token => _client.Subscribe(new SubscribeRequest(), cancellationToken: token),
            ProcessEvent, "PipelineConnection", ct));
        _audioLevelTask = Task.Run(() => ReadSubscriptionAsync(
            token => _client.SubscribeAudioLevel(new SubscribeRequest(), cancellationToken: token),
            level =>
            {
                Volatile.Write(ref _audioLevel, level.Level);
                Volatile.Write(ref _audioLevelTimestamp, Stopwatch.GetTimestamp());
            }, "AudioLevelConnection", ct));
    }

    private async Task ReadSubscriptionAsync<T>(
        Func<CancellationToken, AsyncServerStreamingCall<T>> subscribe,
        Action<T> consume, string stage, CancellationToken ct)
    {
        var reportedFailure = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var stream = subscribe(ct);
                    while (await stream.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                    {
                        if (ct.IsCancellationRequested) break;
                        consume(stream.ResponseStream.Current);
                        reportedFailure = false;
                    }
                    if (!ct.IsCancellationRequested)
                        throw new IOException("The pipeline event stream closed unexpectedly.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!reportedFailure && Volatile.Read(ref _disposed) == 0)
                    {
                        reportedFailure = true;
                        ErrorOccurred?.Invoke(this, new PipelineError
                        {
                            Stage = stage,
                            Message = $"本地服务连接中断，正在重连：{ex.Message}",
                            Exception = ex
                        });
                    }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void ProcessEvent(PipelineEvent evt)
    {
        switch (evt.EventCase)
        {
            case PipelineEvent.EventOneofCase.StateChanged:
                _state = MapState(evt.StateChanged.State);
                StateChanged?.Invoke(this, State);
                break;
            case PipelineEvent.EventOneofCase.PartialTranscript:
                PartialTranscriptUpdated?.Invoke(this, evt.PartialTranscript.Text);
                break;
            case PipelineEvent.EventOneofCase.CleanupChunk:
                CleanupChunkProduced?.Invoke(this, evt.CleanupChunk.Text);
                break;
            case PipelineEvent.EventOneofCase.SessionCompleted:
                var result = MapResult(evt.SessionCompleted.Result);
                LastProcessedText = result.ProcessedText;
                HasRecordingData = true;
                SessionCompleted?.Invoke(this, result);
                break;
            case PipelineEvent.EventOneofCase.Error:
                ErrorOccurred?.Invoke(this, new PipelineError
                {
                    Stage = evt.Error.Error.Stage,
                    Message = evt.Error.Error.Message
                });
                break;
            case PipelineEvent.EventOneofCase.LocalCommand:
                var cmd = MapCommand(evt.LocalCommand);
                if (cmd != null) LocalCommandExecuted?.Invoke(this, cmd);
                break;
        }
    }

    private static CommandEvent? MapCommand(LocalCommandEvent evt) => evt.PayloadCase switch
    {
        LocalCommandEvent.PayloadOneofCase.Append => new CommandEvent
        {
            CommandId = evt.CommandId,
            CommandType = CommandType.Append,
            Payload = new AppendCommandPayload { Content = evt.Append.Content }
        },
        LocalCommandEvent.PayloadOneofCase.Replace => new CommandEvent
        {
            CommandId = evt.CommandId,
            CommandType = CommandType.Replace,
            Payload = new ReplaceCommandPayload { OriginalText = evt.Replace.OriginalText, TargetText = evt.Replace.TargetText }
        },
        LocalCommandEvent.PayloadOneofCase.ReadAllContext => new CommandEvent
        {
            CommandId = evt.CommandId,
            CommandType = CommandType.ReadAllContext,
            Payload = new ReadAllContextCommandPayload()
        },
        _ => null
    };

    private static PipelineState MapState(PipelineStateDto state) => state switch
    {
        PipelineStateDto.Idle => PipelineState.Idle,
        PipelineStateDto.Listening => PipelineState.Listening,
        PipelineStateDto.Transcribing => PipelineState.Transcribing,
        PipelineStateDto.PostProcessing => PipelineState.PostProcessing,
        PipelineStateDto.Completed => PipelineState.Completed,
        PipelineStateDto.Error => PipelineState.Error,
        _ => PipelineState.Idle
    };

    private static PipelineResult MapResult(PipelineResultDto dto) => new()
    {
        RawTranscript = dto.RawTranscript,
        ProcessedText = dto.ProcessedText,
        AudioDuration = TimeSpan.FromTicks(dto.AudioDurationTicks),
        Timestamp = new DateTime(dto.TimestampTicks),
        Language = dto.Language,
        Mode = MapMode(dto.Mode)
    };

    private static SessionModeDto MapMode(SessionMode mode) => mode switch
    {
        SessionMode.Instruct => SessionModeDto.Instruct,
        _ => SessionModeDto.Transcribe
    };

    private static SessionMode MapMode(SessionModeDto mode) => mode switch
    {
        SessionModeDto.Instruct => SessionMode.Instruct,
        _ => SessionMode.Transcribe
    };

    public void Dispose()
    {
        _ = BeginDispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync() => new(BeginDispose());

    private Task BeginDispose()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is not null)
                return _disposeTask;

            Volatile.Write(ref _disposed, 1);
            var cts = _subscribeCts;
            cts?.Cancel();
            _channel.Dispose();
            Volatile.Write(ref _audioLevel, 0);
            _disposeTask = FinishSubscriptionsAsync(cts);
            _ = _disposeTask.ContinueWith(
                task => Trace.TraceError("Pipeline subscription cleanup failed: {0}", task.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return _disposeTask;
        }
    }

    private async Task FinishSubscriptionsAsync(CancellationTokenSource? cts)
    {
        try
        {
            await Task.WhenAll(_subscribeTask ?? Task.CompletedTask, _audioLevelTask ?? Task.CompletedTask)
                .ConfigureAwait(false);
        }
        finally
        {
            cts?.Dispose();
        }
    }
}
