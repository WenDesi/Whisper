using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Models;

namespace WhisperDesk.Core.Providers.Stt.Volcengine;

/// <summary>
/// Volcengine Doubao bigmodel streaming STT provider.
/// Uses the binary WebSocket protocol at wss://openspeech.bytedance.com/api/v3/sauc/bigmodel.
/// Receives audio via PushAudio(), emits partial/final results via events.
/// Does NOT own microphone capture -- that's AudioRouter's job.
/// </summary>
public class VolcengineSttProvider : IStreamingSttProvider
{
    private const string WebSocketEndpoint = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel";

    // Binary protocol constants
    private const byte ProtocolVersion = 0x1;
    private const byte HeaderSizeUnits = 0x1; // 1 * 4 = 4 bytes header
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

    // Thread-safe result accumulation
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

        // Connect WebSocket with auth headers
        _webSocket = new ClientWebSocket();
        var connectId = Guid.NewGuid().ToString(); // dashed UUID

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

        // Send the full client request (first message)
        await SendFullClientRequestAsync(options);

        // Start background loops
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token), _sessionCts.Token);
        _sendLoopTask = Task.Run(() => SendLoopAsync(_sessionCts.Token), _sessionCts.Token);

        _logger.LogInformation("[Volcengine] Session started. Ready for audio.");
    }

    public void PushAudio(ReadOnlyMemory<byte> audioData)
    {
        if (_audioChannel == null || _audioChannel.Writer.TryWrite(new AudioChunk(audioData.ToArray(), IsLast: false)) == false)
        {
            // Channel full or closed -- drop the chunk (backpressure)
            _logger.LogWarning("[Volcengine] Audio channel full or closed, dropping chunk of {Size} bytes.", audioData.Length);
        }
    }

    public void SignalEndOfAudio()
    {
        _logger.LogInformation("[Volcengine] End of audio signaled.");
        // Write a sentinel value to signal the send loop to send the last-audio frame
        _audioChannel?.Writer.TryWrite(new AudioChunk([], IsLast: true));
        _audioChannel?.Writer.TryComplete();
    }

    public async Task<string> EndSessionAsync()
    {
        _logger.LogInformation("[Volcengine] Ending session...");

        // Wait for the session to complete (server sends is_last_package=true)
        if (_sessionCompleteTcs != null)
        {
            await Task.WhenAny(_sessionCompleteTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        // Cancel background loops
        _sessionCts?.Cancel();

        // Wait for loops to finish gracefully
        try
        {
            if (_sendLoopTask != null)
                await _sendLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Expected during shutdown
        }

        try
        {
            if (_receiveLoopTask != null)
                await _receiveLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Expected during shutdown
        }

        // Close WebSocket
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
            // Swallow disposal errors
        }

        _sessionCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Binary Protocol

    /// <summary>
    /// Builds a Volcengine binary frame: 4-byte header + 4-byte payload size + payload.
    /// </summary>
    private static byte[] BuildFrame(
        byte messageType,
        byte messageTypeFlags,
        byte serialization,
        byte compression,
        ReadOnlySpan<byte> payload)
    {
        // Header: 4 bytes
        // Byte 0: (protocol_version << 4) | header_size_in_4byte_units
        // Byte 1: (message_type << 4) | message_type_specific_flags
        // Byte 2: (serialization_method << 4) | compression_type
        // Byte 3: reserved (0x00)
        // Then: 4-byte big-endian payload size
        // Then: payload bytes

        var totalSize = 4 + 4 + payload.Length;
        var frame = new byte[totalSize];

        frame[0] = (byte)((ProtocolVersion << 4) | HeaderSizeUnits);
        frame[1] = (byte)((messageType << 4) | (messageTypeFlags & 0x0F));
        frame[2] = (byte)((serialization << 4) | (compression & 0x0F));
        frame[3] = 0x00; // reserved

        // Payload size (big-endian)
        WriteInt32BigEndian(frame.AsSpan(4), payload.Length);

        // Payload
        payload.CopyTo(frame.AsSpan(8));

        return frame;
    }

    /// <summary>
    /// Compresses data using gzip.
    /// </summary>
    private static byte[] GzipCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Decompresses gzip data.
    /// </summary>
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
        // Audio-only message: type 0x2, no serialization, no compression
        // For the last audio packet, set message_type_flags to signal end-of-audio.
        // Convention: flags = 0x02 for last packet (sequence negative indicator)
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
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Volcengine] Error in send loop.");
            ErrorOccurred?.Invoke(this, new SttError("SendError", ex.Message, ex));
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // Server responses can be up to ~64KB; use a pooled buffer
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
            // Expected during shutdown
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

        // Parse header — header_size is in 4-byte units (byte 0 low nibble)
        var headerSizeUnits = messageBytes[0] & 0x0F;
        var headerSize = headerSizeUnits * 4; // actual header size in bytes
        var messageType = (byte)((messageBytes[1] >> 4) & 0x0F);
        var compression = (byte)(messageBytes[2] & 0x0F);

        // Server response (0x9) and error (0xF) have a sequence number after the header.
        // Frame layout: [header] [sequence (4 bytes)] [payload_size (4 bytes)] [payload]
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

            // Support both top-level result and payload_msg.result
            var result = response.Result ?? response.PayloadMsg?.Result;

            if (result != null)
            {
                var resultText = result.Text ?? string.Empty;

                // Check for definite (final) utterances
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

                // Emit partial result if no definite utterances and text is non-empty
                if (!hasDefiniteUtterances && !string.IsNullOrWhiteSpace(resultText))
                {
                    _logger.LogDebug("[Volcengine] Partial: {Text}", resultText);
                    PartialResultReceived?.Invoke(this, new SttPartialResult(resultText));
                }
            }

            // Check for session end
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

    /// <summary>Internal audio chunk for the send channel.</summary>
    private record AudioChunk(byte[] Data, bool IsLast);
}

#region Volcengine Protocol JSON Models

// These classes are internal (not nested) so the System.Text.Json source generator can access them.
// They are only used for Volcengine WebSocket protocol serialization.

internal class VolcengineRequest
{
    [JsonPropertyName("user")]
    public VolcengineUserInfo User { get; set; } = new();

    [JsonPropertyName("audio")]
    public VolcengineAudioInfo Audio { get; set; } = new();

    [JsonPropertyName("request")]
    public VolcengineRequestInfo Request { get; set; } = new();
}

internal class VolcengineUserInfo
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "default";
}

internal class VolcengineAudioInfo
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "pcm";

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "raw";

    [JsonPropertyName("rate")]
    public int Rate { get; set; } = 16000;

    [JsonPropertyName("bits")]
    public int Bits { get; set; } = 16;

    [JsonPropertyName("channel")]
    public int Channel { get; set; } = 1;
}

internal class VolcengineRequestInfo
{
    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = "bigmodel";

    [JsonPropertyName("enable_itn")]
    public bool EnableItn { get; set; } = true;

    [JsonPropertyName("enable_punc")]
    public bool EnablePunc { get; set; } = true;

    [JsonPropertyName("result_type")]
    public string ResultType { get; set; } = "single";

    [JsonPropertyName("show_utterances")]
    public bool ShowUtterances { get; set; } = true;
}

internal class VolcengineResponse
{
    // Actual server response structure: {"audio_info":{...},"result":{...}}
    // Some endpoints may wrap in payload_msg — support both.

    [JsonPropertyName("audio_info")]
    public VolcengineAudioInfoResponse? AudioInfo { get; set; }

    [JsonPropertyName("result")]
    public VolcengineRecognitionResult? Result { get; set; }

    [JsonPropertyName("payload_msg")]
    public VolcenginePayloadMessage? PayloadMsg { get; set; }

    [JsonPropertyName("is_last_package")]
    public bool IsLastPackage { get; set; }
}

internal class VolcengineAudioInfoResponse
{
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
}

internal class VolcenginePayloadMessage
{
    [JsonPropertyName("is_end")]
    public bool IsEnd { get; set; }

    [JsonPropertyName("result")]
    public VolcengineRecognitionResult? Result { get; set; }
}

internal class VolcengineRecognitionResult
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("utterances")]
    public List<VolcengineUtterance>? Utterances { get; set; }
}

internal class VolcengineUtterance
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("start_time")]
    public int StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public int EndTime { get; set; }

    [JsonPropertyName("definite")]
    public bool Definite { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for Volcengine protocol models.
/// Avoids reflection-based serialization for AOT compatibility and performance.
/// </summary>
[JsonSerializable(typeof(VolcengineRequest))]
[JsonSerializable(typeof(VolcengineResponse))]
internal partial class VolcengineJsonContext : JsonSerializerContext;

#endregion
