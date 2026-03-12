using System.IO;
using NAudio.Wave;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

public class AudioRecorderService : IDisposable
{
    private readonly ILogger<AudioRecorderService> _logger;
    private readonly AudioSettings _audioSettings;
    private WaveInEvent? _waveIn;
    private MemoryStream? _memoryStream;
    private WaveFileWriter? _waveWriter;
    private bool _isRecording;

    public bool IsRecording => _isRecording;

    public event EventHandler<float>? AudioLevelChanged;

    public AudioRecorderService(ILogger<AudioRecorderService> logger, AudioSettings audioSettings)
    {
        _logger = logger;
        _audioSettings = audioSettings;
    }

    public void StartRecording()
    {
        if (_isRecording) return;

        _memoryStream = new MemoryStream();
        var waveFormat = new WaveFormat(_audioSettings.SampleRate, _audioSettings.BitsPerSample, _audioSettings.Channels);

        _waveIn = new WaveInEvent
        {
            WaveFormat = waveFormat,
            BufferMilliseconds = 50
        };

        _waveWriter = new WaveFileWriter(_memoryStream, waveFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        _isRecording = true;

        _logger.LogInformation("Recording started (sample rate: {SampleRate}, channels: {Channels})",
            _audioSettings.SampleRate, _audioSettings.Channels);
    }

    public byte[] StopRecording()
    {
        if (!_isRecording || _waveIn == null) return [];

        _waveIn.StopRecording();
        _isRecording = false;

        _waveWriter?.Flush();

        var audioData = _memoryStream?.ToArray() ?? [];
        _logger.LogInformation("Recording stopped. Audio size: {Size} bytes", audioData.Length);

        CleanupRecording();
        return audioData;
    }

    public static byte[] LoadAudioFile(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        using var memoryStream = new MemoryStream();
        using var waveWriter = new WaveFileWriter(memoryStream, new WaveFormat(16000, 16, 1));

        // Resample to 16kHz mono if needed
        var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1));
        byte[] buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            waveWriter.Write(buffer, 0, bytesRead);
        }

        waveWriter.Flush();
        return memoryStream.ToArray();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);

        // Calculate audio level for visualization
        float maxLevel = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = (short)(e.Buffer[i + 1] << 8 | e.Buffer[i]);
            float sampleLevel = Math.Abs(sample / 32768f);
            if (sampleLevel > maxLevel) maxLevel = sampleLevel;
        }
        AudioLevelChanged?.Invoke(this, maxLevel);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Recording error occurred");
        }
    }

    private void CleanupRecording()
    {
        _waveWriter?.Dispose();
        _waveWriter = null;
        _waveIn?.Dispose();
        _waveIn = null;
        // Don't dispose _memoryStream here - we return its data
        _memoryStream = null;
    }

    public void Dispose()
    {
        if (_isRecording) StopRecording();
        CleanupRecording();
        GC.SuppressFinalize(this);
    }
}
