using System.Collections.Concurrent;
using MethodTimer;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.Core.Models;

namespace WhisperDesk.Core.Pipeline;

/// <summary>
/// Unified audio capture + buffering + routing.
/// Starts NAudio mic immediately, buffers while consumer isn't ready,
/// then flushes + streams live audio. Supports optional interceptors.
/// </summary>
public class AudioRouter : IDisposable
{
    private readonly ILogger<AudioRouter> _logger;
    private readonly IEnumerable<IAudioInterceptor> _interceptors;

    private WaveInEvent? _waveIn;
    private Action<ReadOnlyMemory<byte>>? _audioSink;

    // Pre-connection buffer (audio captured before sink is ready)
    private readonly ConcurrentQueue<byte[]> _preBuffer = new();
    private volatile bool _sinkReady;

    // Recording buffer for WAV export
    private MemoryStream? _recordingBuffer;
    private readonly object _recordingLock = new();

    // Store the audio format from Start() for WAV header generation
    private AudioFormat _currentFormat = AudioFormat.Default;

    // Chunk counter for sampled tracing on OnDataAvailable
    private int _chunkCount;

    public AudioRouter(ILogger<AudioRouter> logger, IEnumerable<IAudioInterceptor> interceptors)
    {
        _logger = logger;
        _interceptors = interceptors.OrderBy(i => i.Order).ToList();
    }

    /// <summary>
    /// Start microphone capture. Audio is buffered until SetSink is called.
    /// </summary>
    [Time]
    public void Start(AudioFormat format)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        _sinkReady = false;
        _currentFormat = format;
        _chunkCount = 0;

        lock (_recordingLock)
        {
            _recordingBuffer?.Dispose();
            _recordingBuffer = new MemoryStream();
        }

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels),
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();

        _logger.LogInformation("[AudioRouter] Mic capture started ({SampleRate}Hz, {Bits}bit, {Ch}ch). Buffering until sink ready.",
            format.SampleRate, format.BitsPerSample, format.Channels);
    }

    /// <summary>
    /// Set the audio sink (typically the STT provider's PushAudio method).
    /// Flushes any pre-buffered audio immediately.
    /// </summary>
    [Time]
    public void SetSink(Action<ReadOnlyMemory<byte>> sink)
    {
        using var _span = MethodTimeLogger.BeginSpan();

        _audioSink = sink;
        _sinkReady = true;
        FlushPreBuffer();
    }

    /// <summary>Stop microphone capture.</summary>
    [Time]
    public void Stop()
    {
        using var _span = MethodTimeLogger.BeginSpan();

        if (_waveIn != null)
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _sinkReady = false;
        _audioSink = null;

        _logger.LogInformation("[AudioRouter] Mic capture stopped.");
    }

    /// <summary>True if recording data is available for WAV export.</summary>
    public bool HasRecordingData
    {
        get
        {
            lock (_recordingLock)
            {
                return _recordingBuffer != null && _recordingBuffer.Length > 0;
            }
        }
    }

    /// <summary>Get captured audio as a WAV byte array. Returns null if no data.</summary>
    [Time]
    public byte[]? GetRecordingAsWav()
    {
        using var _span = MethodTimeLogger.BeginSpan();

        byte[] pcmData;
        lock (_recordingLock)
        {
            if (_recordingBuffer == null || _recordingBuffer.Length == 0)
            {
                _logger.LogWarning("[AudioRouter] No recording data available.");
                return null;
            }
            pcmData = _recordingBuffer.ToArray();
        }

        _logger.LogInformation("[AudioRouter] Building WAV from {Bytes} bytes of PCM.", pcmData.Length);

        using var wavStream = new MemoryStream();
        using (var writer = new BinaryWriter(wavStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            int sampleRate = _currentFormat.SampleRate;
            short bitsPerSample = (short)_currentFormat.BitsPerSample;
            short channels = (short)_currentFormat.Channels;
            int byteRate = _currentFormat.ByteRate;
            short blockAlign = (short)_currentFormat.BlockAlign;

            // RIFF header
            writer.Write("RIFF"u8);
            writer.Write(36 + pcmData.Length);
            writer.Write("WAVE"u8);

            // fmt sub-chunk
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);

            // data sub-chunk
            writer.Write("data"u8);
            writer.Write(pcmData.Length);
            writer.Write(pcmData);
        }

        return wavStream.ToArray();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        _chunkCount++;

        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        // Always tee to recording buffer
        lock (_recordingLock)
        {
            _recordingBuffer?.Write(chunk, 0, chunk.Length);
        }

        // Run through interceptors
        ReadOnlyMemory<byte> audio = chunk;
        foreach (var interceptor in _interceptors)
        {
            audio = interceptor.Process(audio);
        }

        if (_sinkReady)
        {
            _audioSink?.Invoke(audio);
        }
        else
        {
            _preBuffer.Enqueue(audio.ToArray());
        }
    }

    [Time]
    private void FlushPreBuffer()
    {
        using var _span = MethodTimeLogger.BeginSpan();

        int flushed = 0;
        while (_preBuffer.TryDequeue(out var chunk))
        {
            _audioSink?.Invoke(chunk);
            flushed++;
        }

        if (flushed > 0)
        {
            _logger.LogInformation("[AudioRouter] Flushed {Count} pre-buffered chunks to sink.", flushed);
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_recordingLock)
        {
            _recordingBuffer?.Dispose();
            _recordingBuffer = null;
        }
        GC.SuppressFinalize(this);
    }
}
