using System.Threading.Channels;
using Grpc.Core;
using WhisperDesk.Core.Contract;
using WhisperDesk.Proto;

namespace WhisperDesk.Server;

public class PipelineGrpcService : PipelineService.PipelineServiceBase
{
    private readonly IPipelineController _pipeline;
    private readonly CancellationToken _shutdownToken;

    public PipelineGrpcService(IPipelineController pipeline, CancellationTokenSource shutdownCts)
    {
        _pipeline = pipeline;
        _shutdownToken = shutdownCts.Token;
    }

    public override async Task<StartSessionResponse> StartSession(StartSessionRequest request, ServerCallContext context)
    {
        await _pipeline.StartSessionAsync(request.ForegroundProcess, request.ForegroundWindowTitle, context.CancellationToken);
        return new StartSessionResponse();
    }

    public override async Task<StopSessionResponse> StopSession(StopSessionRequest request, ServerCallContext context)
    {
        var result = await _pipeline.StopSessionAsync(context.CancellationToken);
        var response = new StopSessionResponse();
        if (result != null)
        {
            response.Result = MapResult(result);
        }
        return response;
    }

    public override async Task<AbortSessionResponse> AbortSession(AbortSessionRequest request, ServerCallContext context)
    {
        await _pipeline.AbortSessionAsync();
        return new AbortSessionResponse();
    }

    public override async Task<CorrectResponse> CorrectLastResult(CorrectRequest request, ServerCallContext context)
    {
        var corrected = await _pipeline.CorrectLastResultAsync(request.CorrectionTranscript, context.CancellationToken);
        return new CorrectResponse { CorrectedText = corrected ?? "" };
    }

    public override Task<GetStateResponse> GetState(GetStateRequest request, ServerCallContext context)
    {
        var response = new GetStateResponse
        {
            State = MapState(_pipeline.State),
            HasRecordingData = _pipeline.HasRecordingData
        };
        if (_pipeline.LastProcessedText != null)
        {
            response.LastProcessedText = _pipeline.LastProcessedText;
        }
        return Task.FromResult(response);
    }

    public override Task<GetRecordingWavResponse> GetRecordingWav(GetRecordingWavRequest request, ServerCallContext context)
    {
        var wav = _pipeline.GetRecordingAsWav();
        return Task.FromResult(new GetRecordingWavResponse
        {
            WavData = wav != null ? Google.Protobuf.ByteString.CopyFrom(wav) : Google.Protobuf.ByteString.Empty
        });
    }

    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<PipelineEvent> responseStream, ServerCallContext context)
    {
        var channel = Channel.CreateUnbounded<PipelineEvent>();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _shutdownToken);
        var ct = linkedCts.Token;

        void OnStateChanged(object? s, PipelineState state) =>
            channel.Writer.TryWrite(new PipelineEvent { StateChanged = new StateChangedEvent { State = MapState(state) } });

        void OnPartial(object? s, string text) =>
            channel.Writer.TryWrite(new PipelineEvent { PartialTranscript = new PartialTranscriptEvent { Text = text } });

        void OnCompleted(object? s, PipelineResult result) =>
            channel.Writer.TryWrite(new PipelineEvent { SessionCompleted = new SessionCompletedEvent { Result = MapResult(result) } });

        void OnError(object? s, PipelineError error) =>
            channel.Writer.TryWrite(new PipelineEvent { Error = new ErrorEvent { Error = new PipelineErrorDto { Stage = error.Stage, Message = error.Message } } });

        _pipeline.StateChanged += OnStateChanged;
        _pipeline.PartialTranscriptUpdated += OnPartial;
        _pipeline.SessionCompleted += OnCompleted;
        _pipeline.ErrorOccurred += OnError;

        ct.Register(() => channel.Writer.TryComplete());

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                await responseStream.WriteAsync(evt);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _pipeline.StateChanged -= OnStateChanged;
            _pipeline.PartialTranscriptUpdated -= OnPartial;
            _pipeline.SessionCompleted -= OnCompleted;
            _pipeline.ErrorOccurred -= OnError;
        }
    }

    private static PipelineStateDto MapState(PipelineState state) => state switch
    {
        PipelineState.Idle => PipelineStateDto.Idle,
        PipelineState.Listening => PipelineStateDto.Listening,
        PipelineState.Transcribing => PipelineStateDto.Transcribing,
        PipelineState.PostProcessing => PipelineStateDto.PostProcessing,
        PipelineState.Completed => PipelineStateDto.Completed,
        PipelineState.Error => PipelineStateDto.Error,
        _ => PipelineStateDto.Idle
    };

    private static PipelineResultDto MapResult(PipelineResult result) => new()
    {
        RawTranscript = result.RawTranscript,
        ProcessedText = result.ProcessedText,
        AudioDurationTicks = result.AudioDuration.Ticks,
        TimestampTicks = result.Timestamp.Ticks,
        Language = result.Language ?? ""
    };
}
