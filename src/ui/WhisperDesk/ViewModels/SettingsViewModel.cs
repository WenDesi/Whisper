using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.Logging;
using WhisperDesk.Server;

namespace WhisperDesk.ViewModels;

/// <summary>
/// ViewModel for the Settings dialog. Shows available microphones with
/// real-time volume meters and lets the user pick one.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly GrpcDeviceClient _deviceClient;
    private readonly ILogger _logger;
    private readonly string _currentDeviceId;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initializeTask;
    private Task? _volumeTask;
    private Task? _disposeTask;
    private bool _monitoringRequested;

    public ObservableCollection<MicrophoneItem> Devices { get; } = new();

    [ObservableProperty]
    private bool _noDevicesFound;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    private bool CanApply => !IsLoading && !HasError && Devices.Count > 0;

    /// <summary>The WASAPI device ID of the currently selected mic, or null if none selected.</summary>
    public string? SelectedDeviceId => Devices.FirstOrDefault(d => d.IsSelected)?.Id;

    /// <summary>True when Apply was clicked (signals the caller to save).</summary>
    public bool Applied { get; private set; }

    public SettingsViewModel(GrpcDeviceClient deviceClient, string currentDeviceId, ILogger logger)
    {
        _deviceClient = deviceClient;
        _currentDeviceId = currentDeviceId;
        _logger = logger;
    }

    public Task InitializeAsync() => _initializeTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            var devices = await _deviceClient.GetCaptureDevicesAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            LoadDevices(devices);

            if (devices.Count > 0)
            {
                // Finish startup before cleanup, even if the dialog closes in the meantime.
                _monitoringRequested = true;
                await _deviceClient.StartMonitoringAsync();
                _lifetime.Token.ThrowIfCancellationRequested();
                _volumeTask = MonitorVolumesAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] Failed to initialize microphone monitoring.");
            ErrorMessage = $"无法读取麦克风：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadDevices(IReadOnlyList<CaptureDeviceInfo> devices)
    {
        NoDevicesFound = devices.Count == 0;

        foreach (var d in devices)
        {
            bool shouldSelect = !string.IsNullOrEmpty(_currentDeviceId)
                ? d.Id == _currentDeviceId
                : d.IsDefault;

            var item = new MicrophoneItem
            {
                Id = d.Id,
                DisplayName = d.IsDefault ? $"{d.Name}（系统默认）" : d.Name,
                IsSelected = shouldSelect
            };
            Devices.Add(item);
        }

        // If nothing was selected (e.g., saved device was disconnected), select the first/default
        if (Devices.Count > 0 && !Devices.Any(d => d.IsSelected))
        {
            Devices[0].IsSelected = true;
        }
    }

    private async Task MonitorVolumesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var device in Devices)
                {
                    var peak = await _deviceClient.GetPeakVolumeAsync(device.Id, ct);
                    ct.ThrowIfCancellationRequested();
                    device.Volume = (int)(Math.Clamp(peak, 0, 1) * 100);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] Failed to read microphone volume.");
            ErrorMessage = $"无法读取麦克风音量：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        Applied = true;
        // Close the MaterialDesign DialogHost with "true" result
        DialogHost.CloseDialogCommand.Execute(true, null);
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (_initializeTask is not null)
                await _initializeTask;
            if (_volumeTask is not null)
                await _volumeTask;
            if (_monitoringRequested)
                await _deviceClient.StopMonitoringAsync();
        }
        finally
        {
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>
/// Represents a single microphone device in the Settings dialog device list.
/// </summary>
public partial class MicrophoneItem : ObservableObject
{
    /// <summary>WASAPI device ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name (may include "(Default)" suffix).</summary>
    public string DisplayName { get; init; } = "";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Current volume level 0-100 for the progress bar.</summary>
    [ObservableProperty]
    private int _volume;
}
