using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WhisperDesk.Stt.Contract;

namespace WhisperDesk.Stt.Provider.Volcengine;

public class VolcengineSttProvider : IStreamingSttProvider
{
    private const byte ProtocolVersion = 0x1;
    private const byte HeaderSizeUnits = 0x1;
    private const byte MessageTypeFullClientRequest = 0x1;
    private const byte MessageTypeAudioOnly = 0x2;
    private const byte MessageTypeServerResponse = 0x9;
    private const byte MessageTypeServerError = 0xF;
    private const byte FlagNoSequence = 0x0;
    private const byte FlagPositiveSequence = 0x1;
    private const byte FlagLastNoSequence = 0x2;
    private const byte FlagNegativeSequence = 0x3;
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
    private string _lastPartialText = string.Empty;
    private TaskCompletionSource<bool>? _sessionCompleteTcs;
    private bool _serverSignaledEnd;
    private int _sequence;

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
        _lastPartialText = string.Empty;
        _sessionCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _serverSignaledEnd = false;
        _sequence = 1;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _audioChannel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions
        {
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
            await _webSocket.ConnectAsync(new Uri(_config.Endpoint), _sessionCts.Token);
            _logger.LogInformation(
                "[Volcengine] WebSocket connected. Mode={Mode}, ConnectId={ConnectId}, Endpoint={Endpoint}, EnableNonstream={EnableNonstream}, ResultType={ResultType}, ShowUtterances={ShowUtterances}, EndWindowSizeMs={EndWindowSizeMs}, ForceToSpeechTimeMs={ForceToSpeechTimeMs}",
                _config.Mode,
                connectId,
                _config.Endpoint,
                _config.EnableNonstream,
                _config.ResultType,
                _config.ShowUtterances,
                _config.EndWindowSizeMs,
                _config.ForceToSpeechTimeMs);
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
            _logger.LogWarning("[Volcengine] Audio channel closed, dropping chunk of {Size} bytes.", audioData.Length);
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
            var finalizationTimeout = TimeSpan.FromMilliseconds(Math.Max(500, _config.FinalizationTimeoutMs));
            var completedTask = await Task.WhenAny(_sessionCompleteTcs.Task, Task.Delay(finalizationTimeout));
            if (completedTask == _sessionCompleteTcs.Task)
            {
                _logger.LogDebug("[Volcengine] Session completion signal received.");
            }
            else
            {
                _logger.LogWarning("[Volcengine] Timed out after {TimeoutMs}ms waiting for session completion signal; ending with collected results.",
                    (int)finalizationTimeout.TotalMilliseconds);
            }
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
        if (!string.IsNullOrWhiteSpace(_lastPartialText) && !IsPartialCoveredByFinalText(_lastPartialText, fullText))
        {
            if (string.IsNullOrWhiteSpace(fullText))
            {
                fullText = _lastPartialText;
                _logger.LogWarning("[Volcengine] No final segments received; using last partial transcript ({Length} chars).", fullText.Length);
            }
            else if (_serverSignaledEnd)
            {
                _logger.LogDebug("[Volcengine] Ignoring trailing partial after server end signal ({Length} chars).", _lastPartialText.Length);
            }
            else if (_lastPartialText.StartsWith(fullText, StringComparison.Ordinal))
            {
                var suffix = _lastPartialText[fullText.Length..];
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    fullText += suffix;
                    _logger.LogWarning("[Volcengine] Appended trailing partial suffix ({Length} chars).", suffix.Length);
                }
            }
            else if (!fullText.EndsWith(_lastPartialText, StringComparison.Ordinal))
            {
                fullText += _lastPartialText;
                _logger.LogWarning("[Volcengine] Appended trailing partial segment ({Length} chars).", _lastPartialText.Length);
            }
        }

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
        ReadOnlySpan<byte> payload,
        int? sequence = null)
    {
        var totalSize = 4 + (sequence.HasValue ? 4 : 0) + 4 + payload.Length;
        var frame = new byte[totalSize];

        frame[0] = (byte)((ProtocolVersion << 4) | HeaderSizeUnits);
        frame[1] = (byte)((messageType << 4) | (messageTypeFlags & 0x0F));
        frame[2] = (byte)((serialization << 4) | (compression & 0x0F));
        frame[3] = 0x00;

        var offset = 4;
        if (sequence.HasValue)
        {
            WriteInt32BigEndian(frame.AsSpan(offset), sequence.Value);
            offset += 4;
        }

        WriteInt32BigEndian(frame.AsSpan(offset), payload.Length);
        payload.CopyTo(frame.AsSpan(offset + 4));

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
                EnableNonstream = _config.EnableNonstream,
                EndWindowSize = _config.EndWindowSizeMs,
                ForceToSpeechTime = _config.ForceToSpeechTimeMs,
                ResultType = _config.ResultType,
                ShowUtterances = _config.ShowUtterances,
                Corpus = BuildCorpus(options)
            }
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(requestPayload, VolcengineJsonContext.Default.VolcengineRequest);
        var compressed = GzipCompress(jsonBytes);

        var frame = BuildFrame(
            MessageTypeFullClientRequest,
            FlagPositiveSequence,
            SerializationJson,
            CompressionGzip,
            compressed,
            sequence: _sequence++);

        await _webSocket!.SendAsync(frame, WebSocketMessageType.Binary, true, _sessionCts!.Token);
        _logger.LogDebug("[Volcengine] Sent full client request ({Size} bytes compressed).", compressed.Length);
    }

    private async Task SendAudioFrameAsync(ReadOnlyMemory<byte> audioData, bool isLast)
    {
        var sequence = _sequence++;
        byte flags = isLast ? FlagNegativeSequence : FlagPositiveSequence;
        var payload = isLast ? [] : GzipCompress(audioData.ToArray());

        var frame = BuildFrame(
            MessageTypeAudioOnly,
            flags,
            SerializationNone,
            isLast ? CompressionNone : CompressionGzip,
            payload,
            sequence: isLast ? -sequence : sequence);

        await _webSocket!.SendAsync(frame, WebSocketMessageType.Binary, true, _sessionCts!.Token);
    }

    #endregion

    #region Helpers

    private static VolcengineCorpus? BuildCorpus(SttSessionOptions options)
    {
        var hasHotwords = options.PhraseHints.Count > 0;
        var hasDialogContext = options.DialogContext.Count > 0;

        if (!hasHotwords && !hasDialogContext)
            return null;

        var context = new VolcengineContext();

        if (hasHotwords)
        {
            context.Hotwords = options.PhraseHints
                .Select(w => new VolcengineHotword { Word = w })
                .ToList();
        }

        if (hasDialogContext)
        {
            context.ContextType = "dialog_ctx";
            context.ContextData = options.DialogContext
                .Select(t => new VolcengineContextItem { Text = t.Text })
                .ToList();
        }

        var contextJson = JsonSerializer.Serialize(context, VolcengineJsonContext.Default.VolcengineContext);
        return new VolcengineCorpus { Context = contextJson };
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
        var messageTypeFlags = (byte)(messageBytes[1] & 0x0F);
        var compression = (byte)(messageBytes[2] & 0x0F);

        if (messageBytes.Length < headerSize + 4)
        {
            _logger.LogDebug("[Volcengine] Message type=0x{Type:X}, too short for meta fields.", messageType);
            return;
        }

        var offset = headerSize;
        var sequence = 0;
        var hasSequence = (messageTypeFlags & FlagPositiveSequence) != 0;
        var isLastPackage = (messageTypeFlags & FlagLastNoSequence) != 0;
        if (hasSequence)
        {
            if (messageBytes.Length < offset + 4)
                return;

            sequence = ReadInt32BigEndian(messageBytes.AsSpan(offset));
            offset += 4;
        }

        if ((messageTypeFlags & 0x04) != 0)
        {
            if (messageBytes.Length < offset + 4)
                return;

            offset += 4;
        }

        if (messageType == MessageTypeServerError)
        {
            if (messageBytes.Length < offset + 8)
                return;

            var errorCode = ReadInt32BigEndian(messageBytes.AsSpan(offset));
            var errorPayloadSize = ReadInt32BigEndian(messageBytes.AsSpan(offset + 4));
            var errorPayloadOffset = offset + 8;
            if (errorPayloadSize <= 0 || errorPayloadOffset + errorPayloadSize > messageBytes.Length)
                return;

            ProcessServerError(messageBytes.AsSpan(errorPayloadOffset, errorPayloadSize).ToArray(), compression, errorCode);
            return;
        }

        var payloadSize = ReadInt32BigEndian(messageBytes.AsSpan(offset));
        var payloadOffset = offset + 4;

        if (payloadSize <= 0 || payloadOffset + payloadSize > messageBytes.Length)
        {
            return;
        }

        var payload = messageBytes.AsSpan(payloadOffset, payloadSize).ToArray();

        switch (messageType)
        {
            case MessageTypeServerResponse:
                ProcessServerResponse(payload, compression, messageTypeFlags, sequence, isLastPackage);
                break;

            default:
                _logger.LogDebug("[Volcengine] Received unknown message type: 0x{Type:X}", messageType);
                break;
        }
    }

    private void ProcessServerResponse(byte[] payload, byte compression, byte messageTypeFlags, int sequence, bool isLastPackage)
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
            var isTerminalResponse = isLastPackage || response.IsLastPackage || (response.PayloadMsg?.IsEnd ?? false) || sequence < 0;

            if (result != null)
            {
                var resultText = result.Text ?? string.Empty;

                var hasDefiniteUtterances = false;
                if (result.Utterances != null)
                {
                    foreach (var utterance in result.Utterances)
                    {
                        if (utterance.Definite || isTerminalResponse)
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

                                if (IsPartialCoveredByFinalText(_lastPartialText, text))
                                {
                                    _lastPartialText = string.Empty;
                                }
                            }
                        }
                    }
                }

                if (!hasDefiniteUtterances && isTerminalResponse && !string.IsNullOrWhiteSpace(resultText))
                {
                    _results.Enqueue(resultText);
                    _lastPartialText = string.Empty;
                    _logger.LogInformation("[Volcengine] Final text: {Text}", resultText);
                    FinalResultReceived?.Invoke(this, new SttFinalResult(resultText, TimeSpan.Zero, TimeSpan.Zero));
                }
                else if (!hasDefiniteUtterances && !string.IsNullOrWhiteSpace(resultText))
                {
                    _lastPartialText = resultText;
                    _logger.LogDebug("[Volcengine] Partial: {Text}", resultText);
                    PartialResultReceived?.Invoke(this, new SttPartialResult(resultText));
                }
            }

            if (isLastPackage || response.IsLastPackage || (response.PayloadMsg?.IsEnd ?? false))
            {
                _serverSignaledEnd = true;
                _logger.LogInformation("[Volcengine] Server signaled end of session.");
                _sessionCompleteTcs?.TrySetResult(true);
            }

            if (sequence < 0)
            {
                _logger.LogInformation("[Volcengine] Server response sequence signaled final package. Flags=0x{Flags:X}, Sequence={Sequence}.",
                    messageTypeFlags, sequence);
                _sessionCompleteTcs?.TrySetResult(true);
            }
            else if (messageTypeFlags != 0)
            {
                _logger.LogDebug("[Volcengine] Server response metadata: Flags=0x{Flags:X}, Sequence={Sequence}.",
                    messageTypeFlags, sequence);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Volcengine] Failed to parse server response JSON.");
        }
    }

    private void ProcessServerError(byte[] payload, byte compression, int errorCode)
    {
        try
        {
            var jsonBytes = compression == CompressionGzip ? GzipDecompress(payload) : payload;
            var errorText = Encoding.UTF8.GetString(jsonBytes);
            _logger.LogError("[Volcengine] Server error {Code}: {Error}", errorCode, errorText);
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

    private static bool IsPartialCoveredByFinalText(string partialText, string finalText)
    {
        if (string.IsNullOrWhiteSpace(partialText) || string.IsNullOrWhiteSpace(finalText))
            return false;

        var normalizedPartial = NormalizeForPartialComparison(partialText);
        var normalizedFinal = NormalizeForPartialComparison(finalText);

        return normalizedFinal.Contains(normalizedPartial, StringComparison.Ordinal);
    }

    private static string NormalizeForPartialComparison(string text)
    {
        var trimmed = text.Trim().TrimEnd('?', '!', '.', ',', ';', ':', '？', '！', '。', '，', '；', '：');
        return string.Concat(trimmed.Where(c => !char.IsWhiteSpace(c)));
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
