using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using WhisperDesk.Models;

namespace WhisperDesk.ViewModels;

public partial class EvalDialogViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<EvalDialogViewModel> _logger;
    private readonly string _savePath;
    private readonly byte[] _audioData;

    private WaveOutEvent? _waveOut;
    private WaveFileReader? _waveReader;
    private MemoryStream? _audioStream;
    private System.Windows.Threading.DispatcherTimer? _positionTimer;

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    private string _correctedText = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _playbackPosition;

    [ObservableProperty]
    private double _playbackDuration;

    [ObservableProperty]
    private string _playbackTimeText = "00:00 / 00:00";

    [ObservableProperty]
    private string? _savedFilePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSaved;

    public bool CanSave => !IsSaved;

    public EvalDialogViewModel(
        ILogger<EvalDialogViewModel> logger,
        byte[] audioData,
        string rawText,
        string savePath)
    {
        _logger = logger;
        _audioData = audioData;
        _savePath = savePath;

        RawText = rawText;
        CorrectedText = rawText; // Pre-populate with raw text for editing

        InitializePlayback();
    }

    private void InitializePlayback()
    {
        try
        {
            _audioStream = new MemoryStream(_audioData);
            _waveReader = new WaveFileReader(_audioStream);

            PlaybackDuration = _waveReader.TotalTime.TotalSeconds;
            UpdateTimeText();

            // Timer to update playback position
            _positionTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _positionTimer.Tick += (_, _) =>
            {
                if (_waveReader != null && _waveOut != null && IsPlaying)
                {
                    PlaybackPosition = _waveReader.CurrentTime.TotalSeconds;
                    UpdateTimeText();
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvalDialog] Failed to initialize audio playback");
        }
    }

    private void UpdateTimeText()
    {
        var current = TimeSpan.FromSeconds(PlaybackPosition);
        var total = TimeSpan.FromSeconds(PlaybackDuration);
        PlaybackTimeText = $"{current:mm\\:ss} / {total:mm\\:ss}";
    }

    [RelayCommand]
    private void TogglePlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        try
        {
            // Reset to beginning if at end
            if (_waveReader != null && _waveReader.CurrentTime >= _waveReader.TotalTime - TimeSpan.FromMilliseconds(100))
            {
                _waveReader.Position = 0;
                PlaybackPosition = 0;
            }

            _waveOut?.Dispose();
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_waveReader);

            _waveOut.PlaybackStopped += (_, _) =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsPlaying = false;
                    _positionTimer?.Stop();
                    if (_waveReader != null)
                    {
                        PlaybackPosition = _waveReader.CurrentTime.TotalSeconds;
                        UpdateTimeText();
                    }
                });
            };

            _waveOut.Play();
            IsPlaying = true;
            _positionTimer?.Start();

            _logger.LogDebug("[EvalDialog] Audio playback started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvalDialog] Failed to start audio playback");
        }
    }

    private void StopPlayback()
    {
        try
        {
            _waveOut?.Stop();
            IsPlaying = false;
            _positionTimer?.Stop();

            _logger.LogDebug("[EvalDialog] Audio playback stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvalDialog] Failed to stop audio playback");
        }
    }

    [RelayCommand]
    private void SaveEval()
    {
        try
        {
            // Stop playback if playing
            if (IsPlaying) StopPlayback();

            Directory.CreateDirectory(_savePath);

            var weval = new WevalFile
            {
                Timestamp = DateTime.UtcNow,
                CorrectedText = CorrectedText,
                AudioData = _audioData
            };

            var fileName = $"{DateTime.Now:yyyy-MM-dd_HHmmss}.weval";
            var filePath = Path.Combine(_savePath, fileName);

            weval.Save(filePath);

            SavedFilePath = filePath;
            IsSaved = true;

            _logger.LogInformation("[EvalDialog] Evaluation saved to {FilePath} ({AudioBytes} audio bytes)",
                filePath, _audioData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvalDialog] Failed to save evaluation");
            throw; // Let the dialog handle the error display
        }
    }

    public void Dispose()
    {
        _positionTimer?.Stop();
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _waveReader?.Dispose();
        _waveReader = null;
        _audioStream?.Dispose();
        _audioStream = null;
        GC.SuppressFinalize(this);
    }
}
