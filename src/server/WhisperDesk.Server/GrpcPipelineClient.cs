using Grpc.Net.Client;
using WhisperDesk.Core.Contract;
using WhisperDesk.Proto;

namespace WhisperDesk.Server;

public class GrpcPipelineClient : IPipelineController, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly PipelineService.PipelineServiceClient _client;
    private CancellationTokenSource? _subscribeCts;
    private Task? _subscribeTask;
    private int _disposed;

    public PipelineState State { get; private set; } = PipelineState.Idle;
    public string? LastProcessedText { get; private set; }
    public bool HasRecordingData { get; private set; }

    public event EventHandler<PipelineState>? StateChanged;
    public event EventHandler<string>? PartialTranscriptUpdated;
    public event EventHandler<PipelineResult>? SessionCompleted;
    public event EventHandler<PipelineError>? ErrorOccurred;

    public GrpcPipelineClient(string address)
    {
        _channel = GrpcChannel.ForAddress(address);
        _client = new PipelineService.PipelineServiceClient(_channel);
        StartEventSubscription();
    }

    public async Task StartSessionAsync(string foregroundProcess = "", string foregroundWindowTitle = "", CancellationToken ct = default)
    {
        await _client.StartSessionAsync(new StartSessionRequest
        {
            ForegroundProcess = foregroundProcess,
            ForegroundWindowTitle = foregroundWindowTitle
        }, cancellationToken: ct);
    }

    public async Task<PipelineResult?> StopSessionAsync(CancellationToken ct = default)
    {
        var response = await _client.StopSessionAsync(new StopSessionRequest(), cancellationToken: ct);
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

    public byte[]? GetRecordingAsWav()
    {
        var response = _client.GetRecordingWav(new GetRecordingWavRequest());
        return response.WavData.IsEmpty ? null : response.WavData.ToByteArray();
    }

    private void StartEventSubscription()
    {
        var subscribeCts = new CancellationTokenSource();
        _subscribeCts = subscribeCts;
        _subscribeTask = Task.Run(async () =>
        {
            while (!subscribeCts.Token.IsCancellationRequested)
            {
                try
                {
                    using var stream = _client.Subscribe(new SubscribeRequest(), cancellationToken: subscribeCts.Token);
                    while (await stream.ResponseStream.MoveNext(subscribeCts.Token))
                    {
                        ProcessEvent(stream.ResponseStream.Current);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    await Task.Delay(1000, subscribeCts.Token);
                }
            }
        }, subscribeCts.Token);
    }

    private void ProcessEvent(PipelineEvent evt)
    {
        switch (evt.EventCase)
        {
            case PipelineEvent.EventOneofCase.StateChanged:
                State = MapState(evt.StateChanged.State);
                StateChanged?.Invoke(this, State);
                break;
            case PipelineEvent.EventOneofCase.PartialTranscript:
                PartialTranscriptUpdated?.Invoke(this, evt.PartialTranscript.Text);
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
        }
    }

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
        Language = dto.Language
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var subscribeCts = Interlocked.Exchange(ref _subscribeCts, null);
        var subscribeTask = Interlocked.Exchange(ref _subscribeTask, null);

        try
        {
            subscribeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            subscribeTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException))
        {
        }
        finally
        {
            subscribeCts?.Dispose();
            _channel.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
