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
    private WaveFileWriter? _waveWriter;
    private string? _tempRecordingPath;
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

        var waveFormat = new WaveFormat(_audioSettings.SampleRate, _audioSettings.BitsPerSample, _audioSettings.Channels);

        // Write to a temp file so WAV header is always correct
        _tempRecordingPath = Path.Combine(Path.GetTempPath(), $"whisperdesk_{Guid.NewGuid():N}.wav");
        _waveWriter = new WaveFileWriter(_tempRecordingPath, waveFormat);

        _waveIn = new WaveInEvent
        {
            WaveFormat = waveFormat,
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        _isRecording = true;

        _logger.LogInformation("Recording started (sample rate: {SampleRate}, channels: {Channels}, file: {Path})",
            _audioSettings.SampleRate, _audioSettings.Channels, _tempRecordingPath);
    }

    public byte[] StopRecording()
    {
        if (!_isRecording || _waveIn == null) return [];

        _waveIn.StopRecording();
        _isRecording = false;

        // Dispose writer to finalize WAV header with correct data length
        _waveWriter?.Dispose();
        _waveWriter = null;

        // Read back the complete, valid WAV file
        byte[] audioData = [];
        if (_tempRecordingPath != null && File.Exists(_tempRecordingPath))
        {
            audioData = File.ReadAllBytes(_tempRecordingPath);
            _logger.LogInformation("Recording stopped. Audio size: {Size} bytes, file: {Path}",
                audioData.Length, _tempRecordingPath);

            // Log WAV header info for diagnostics
            if (audioData.Length >= 44)
            {
                var riff = System.Text.Encoding.ASCII.GetString(audioData, 0, 4);
                var fileSize = BitConverter.ToInt32(audioData, 4);
                var dataSize = BitConverter.ToInt32(audioData, 40);
                _logger.LogDebug("WAV header: RIFF={Riff}, FileSize={FileSize}, DataSize={DataSize}",
                    riff, fileSize, dataSize);
            }
        }
        else
        {
            _logger.LogWarning("Recording temp file not found: {Path}", _tempRecordingPath);
        }

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
        if (_waveWriter != null)
        {
            _waveWriter.Dispose();
            _waveWriter = null;
        }
        _waveIn?.Dispose();
        _waveIn = null;
        // Clean up temp recording file
        if (_tempRecordingPath != null)
        {
            try { File.Delete(_tempRecordingPath); } catch { /* ignore */ }
            _tempRecordingPath = null;
        }
    }

    public void Dispose()
    {
        if (_isRecording) StopRecording();
        CleanupRecording();
        GC.SuppressFinalize(this);
    }
}
