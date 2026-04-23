using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WhisperDesk.Stt.Contract;

namespace WhisperDesk.Stt.Volcengine;

public class VolcengineSttProvider : IStreamingSttProvider
{
    private const string WebSocketEndpoint = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel";

    private const byte ProtocolVersion = 0x1;
    private const byte HeaderSizeUnits = 0x1;
    private const byte MessageTypeFullClientRequest = 0x1;
    private const byte MessageTypeAudioOnly = 0x2;
    private const byte MessageTypeServerResponse = 0x9;
    private const byte MessageTypeServerError = 0xF;
    private const byte SerializationJson = 0x1;
    private const byte SerializationNone = 0x0;
    private const byte CompressionGzip = 0x1;
    private const byte CompressionNone = 0x0;

    private readonly ILogger<VolcengineSttProvider> _logger;
    private readonly VolcengineSttConfig _config;

    private ClientWebSocket? _webSocket;
    private Channel<AudioChunk>? _audioChannel;
    private Task? _sendLoopTask;
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _sessionCts;

    private ConcurrentQueue<string> _results = new();
    private TaskCompletionSource<bool>? _sessionCompleteTcs;

    public string Name => "Volcengine Doubao";

    public event EventHandler<SttPartialResult>? PartialResultReceived;
    public event EventHandler<SttFinalResult>? FinalResultReceived;
    public event EventHandler<SttError>? ErrorOccurred;

    public VolcengineSttProvider(ILogger<VolcengineSttProvider> logger, VolcengineSttConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task StartSessionAsync(SttSessionOptions options, CancellationToken ct = default)
    {
        _logger.LogInformation("[Volcengine] Starting session...");

        _results = new ConcurrentQueue<string>();
        _sessionCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _audioChannel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _webSocket = new ClientWebSocket();
        var connectId = Guid.NewGuid().ToString();

        _webSocket.Options.SetRequestHeader("x-api-key", _config.ApiKey);
        _webSocket.Options.SetRequestHeader("X-Api-Resource-Id", _config.ResourceId);
        _webSocket.Options.SetRequestHeader("X-Api-Connect-Id", connectId);

        try
        {
            await _webSocket.ConnectAsync(new Uri(WebSocketEndpoint), _sessionCts.Token);
            _logger.LogInformation("[Volcengine] WebSocket connected. ConnectId={ConnectId}", connectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Volcengine] Failed to connect WebSocket.");
            ErrorOccurred?.Invoke(this, new SttError("ConnectionFailed", ex.Message, ex));
            throw;
        }

        await SendFullClientRequestAsync(options);

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token), _sessionCts.Token);
        _sendLoopTask = Task.Run(() => SendLoopAsync(_sessionCts.Token), _sessionCts.Token);

        _logger.LogInformation("[Volcengine] Session started. Ready for audio.");
    }

    public void PushAudio(ReadOnlyMemory<byte> audioData)
    {
        if (_audioChannel == null || _audioChannel.Writer.TryWrite(new AudioChunk(audioData.ToArray(), IsLast: false)) == false)
        {
            _logger.LogWarning("[Volcengine] Audio channel full or closed, dropping chunk of {Size} bytes.", audioData.Length);
        }
    }

    public void SignalEndOfAudio()
    {
        _logger.LogInformation("[Volcengine] End of audio signaled.");
        _audioChannel?.Writer.TryWrite(new AudioChunk([], IsLast: true));
        _audioChannel?.Writer.TryComplete();
    }

    public async Task<string> EndSessionAsync()
    {
        _logger.LogInformation("[Volcengine] Ending session...");

        if (_sessionCompleteTcs != null)
        {
            await Task.WhenAny(_sessionCompleteTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        _sessionCts?.Cancel();

        try
        {
            if (_sendLoopTask != null)
                await _sendLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        try
        {
            if (_receiveLoopTask != null)
                await _receiveLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        await CloseWebSocketAsync();

        var segments = _results.ToArray();
        var fullText = string.Join("", segments);
        _logger.LogInformation("[Volcengine] Session ended. {Length} chars from {Segments} segments.",
            fullText.Length, segments.Length);

        return fullText;
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        _audioChannel?.Writer.TryComplete();

        try
        {
            _webSocket?.Dispose();
        }
        catch
        {
        }

        _sessionCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Binary Protocol

    private static byte[] BuildFrame(
        byte messageType,
        byte messageTypeFlags,
        byte serialization,
        byte compression,
        ReadOnlySpan<byte> payload)
    {
        var totalSize = 4 + 4 + payload.Length;
        var frame = new byte[totalSize];

        frame[0] = (byte)((ProtocolVersion << 4) | HeaderSizeUnits);
        frame[1] = (byte)((messageType << 4) | (messageTypeFlags & 0x0F));
        frame[2] = (byte)((serialization << 4) | (compression & 0x0F));
        frame[3] = 0x00;

        WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));

        return frame;
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] GzipDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteInt32BigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> source)
    {
        return (source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3];
    }

    #endregion

    #region Session Messages

    private async Task SendFullClientRequestAsync(SttSessionOptions options)
    {
        var requestPayload = new VolcengineRequest
        {
            User = new VolcengineUserInfo { Uid = "whisperdesk" },
            Audio = new VolcengineAudioInfo
            {
                Format = "pcm",
                Codec = "raw",
                Rate = options.AudioFormat.SampleRate,
                Bits = options.AudioFormat.BitsPerSample,
                Channel = options.AudioFormat.Channels
            },
            Request = new VolcengineRequestInfo
            {
                ModelName = "bigmodel",
                EnableItn = true,
                EnablePunc = true,
                ResultType = "single",
                ShowUtterances = true
            }
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(requestPayload, VolcengineJsonContext.Default.VolcengineRequest);
        var compressed = GzipCompress(jsonBytes);

        var frame = BuildFrame(
            MessageTypeFullClientRequest,
            messageTypeFlags: 0x0,
            SerializationJson,
            CompressionGzip,
            compressed);

        await _webSocket!.SendAsync(frame, WebSocketMessageType.Binary, true, _sessionCts!.Token);
        _logger.LogDebug("[Volcengine] Sent full client request ({Size} bytes compressed).", compressed.Length);
    }

    private async Task SendAudioFrameAsync(ReadOnlyMemory<byte> audioData, bool isLast)
    {
        byte flags = isLast ? (byte)0x02 : (byte)0x00;

        var frame = BuildFrame(
            MessageTypeAudioOnly,
            flags,
            SerializationNone,
            CompressionNone,
            audioData.Span);

        await _webSocket!.SendAsync(frame, WebSocketMessageType.Binary, true, _sessionCts!.Token);
    }

    #endregion

    #region Background Loops

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _audioChannel!.Reader.ReadAllAsync(ct))
            {
                if (_webSocket?.State != WebSocketState.Open)
                {
                    _logger.LogWarning("[Volcengine] WebSocket not open, stopping send loop.");
                    break;
                }

                await SendAudioFrameAsync(chunk.Data, chunk.IsLast);

                if (chunk.IsLast)
                {
                    _logger.LogDebug("[Volcengine] Sent last audio frame.");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Volcengine] Error in send loop.");
            ErrorOccurred?.Invoke(this, new SttError("SendError", ex.Message, ex));
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                ValueWebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("[Volcengine] WebSocket closed by server.");
                        _sessionCompleteTcs?.TrySetResult(true);
                        return;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var messageBytes = messageStream.ToArray();
                ProcessServerMessage(messageBytes);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogWarning("[Volcengine] WebSocket closed prematurely.");
            _sessionCompleteTcs?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Volcengine] Error in receive loop.");
            ErrorOccurred?.Invoke(this, new SttError("ReceiveError", ex.Message, ex));
            _sessionCompleteTcs?.TrySetResult(true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ProcessServerMessage(byte[] messageBytes)
    {
        if (messageBytes.Length < 4)
        {
            _logger.LogWarning("[Volcengine] Received message too short ({Length} bytes), ignoring.", messageBytes.Length);
            return;
        }

        var headerSizeUnits = messageBytes[0] & 0x0F;
        var headerSize = headerSizeUnits * 4;
        var messageType = (byte)((messageBytes[1] >> 4) & 0x0F);
        var compression = (byte)(messageBytes[2] & 0x0F);

        var hasSequence = messageType == MessageTypeServerResponse || messageType == MessageTypeServerError;
        var metaSize = hasSequence ? 8 : 4;

        if (messageBytes.Length < headerSize + metaSize)
        {
            _logger.LogDebug("[Volcengine] Message type=0x{Type:X}, too short for meta fields.", messageType);
            return;
        }

        var payloadSizeOffset = hasSequence ? headerSize + 4 : headerSize;
        var payloadSize = ReadInt32BigEndian(messageBytes.AsSpan(payloadSizeOffset));
        var payloadOffset = payloadSizeOffset + 4;

        if (payloadSize <= 0 || payloadOffset + payloadSize > messageBytes.Length)
        {
            return;
        }

        var payload = messageBytes.AsSpan(payloadOffset, payloadSize).ToArray();

        switch (messageType)
        {
            case MessageTypeServerResponse:
                ProcessServerResponse(payload, compression);
                break;

            case MessageTypeServerError:
                ProcessServerError(payload, compression);
                break;

            default:
                _logger.LogDebug("[Volcengine] Received unknown message type: 0x{Type:X}", messageType);
                break;
        }
    }

    private void ProcessServerResponse(byte[] payload, byte compression)
    {
        try
        {
            var jsonBytes = compression == CompressionGzip ? GzipDecompress(payload) : payload;
            var response = JsonSerializer.Deserialize(jsonBytes, VolcengineJsonContext.Default.VolcengineResponse);

            if (response == null)
            {
                _logger.LogWarning("[Volcengine] Failed to deserialize server response.");
                return;
            }

            var result = response.Result ?? response.PayloadMsg?.Result;

            if (result != null)
            {
                var resultText = result.Text ?? string.Empty;

                var hasDefiniteUtterances = false;
                if (result.Utterances != null)
                {
                    foreach (var utterance in result.Utterances)
                    {
                        if (utterance.Definite)
                        {
                            hasDefiniteUtterances = true;
                            var text = utterance.Text ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                _results.Enqueue(text);
                                var offset = TimeSpan.FromMilliseconds(utterance.StartTime);
                                var duration = TimeSpan.FromMilliseconds(utterance.EndTime - utterance.StartTime);
                                _logger.LogInformation("[Volcengine] Final segment: {Text}", text);
                                FinalResultReceived?.Invoke(this, new SttFinalResult(text, offset, duration));
                            }
                        }
                    }
                }

                if (!hasDefiniteUtterances && !string.IsNullOrWhiteSpace(resultText))
                {
                    _logger.LogDebug("[Volcengine] Partial: {Text}", resultText);
                    PartialResultReceived?.Invoke(this, new SttPartialResult(resultText));
                }
            }

            if (response.IsLastPackage || (response.PayloadMsg?.IsEnd ?? false))
            {
                _logger.LogInformation("[Volcengine] Server signaled end of session.");
                _sessionCompleteTcs?.TrySetResult(true);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Volcengine] Failed to parse server response JSON.");
        }
    }

    private void ProcessServerError(byte[] payload, byte compression)
    {
        try
        {
            var jsonBytes = compression == CompressionGzip ? GzipDecompress(payload) : payload;
            var errorText = Encoding.UTF8.GetString(jsonBytes);
            _logger.LogError("[Volcengine] Server error: {Error}", errorText);
            ErrorOccurred?.Invoke(this, new SttError("ServerError", errorText));
            _sessionCompleteTcs?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Volcengine] Failed to parse server error message.");
            ErrorOccurred?.Invoke(this, new SttError("ServerError", "Unparseable error from server", ex));
            _sessionCompleteTcs?.TrySetResult(true);
        }
    }

    #endregion

    #region WebSocket Cleanup

    private async Task CloseWebSocketAsync()
    {
        if (_webSocket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", closeCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Volcengine] WebSocket close failed (non-critical).");
            }
        }

        _webSocket?.Dispose();
        _webSocket = null;
    }

    #endregion

    private record AudioChunk(byte[] Data, bool IsLast);
}
