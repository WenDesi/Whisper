using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using WhisperDesk.Server;

namespace WhisperDesk.ViewModels;

/// <summary>
/// ViewModel for the Settings dialog. Shows available microphones with
/// real-time volume meters and lets the user pick one.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly GrpcDeviceClient _deviceClient;
    private readonly DispatcherTimer _volumeTimer;

    public ObservableCollection<MicrophoneItem> Devices { get; } = new();

    [ObservableProperty]
    private bool _noDevicesFound;

    /// <summary>The WASAPI device ID of the currently selected mic, or null if none selected.</summary>
    public string? SelectedDeviceId => Devices.FirstOrDefault(d => d.IsSelected)?.Id;

    /// <summary>True when Apply was clicked (signals the caller to save).</summary>
    public bool Applied { get; private set; }

    public SettingsViewModel(GrpcDeviceClient deviceClient, string currentDeviceId)
    {
        _deviceClient = deviceClient;

        LoadDevices(currentDeviceId);

        // Start silent capture streams so MasterPeakValue reports live data
        _deviceClient.StartMonitoring();

        // Poll volume levels every 50ms while the dialog is open
        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _volumeTimer.Tick += (_, _) => UpdateVolumes();
        _volumeTimer.Start();
    }

    private void LoadDevices(string currentDeviceId)
    {
        var devices = _deviceClient.GetCaptureDevices();
        NoDevicesFound = devices.Count == 0;

        foreach (var d in devices)
        {
            bool shouldSelect = !string.IsNullOrEmpty(currentDeviceId)
                ? d.Id == currentDeviceId
                : d.IsDefault;

            var item = new MicrophoneItem
            {
                Id = d.Id,
                DisplayName = d.IsDefault ? $"{d.Name} (Default)" : d.Name,
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

    private void UpdateVolumes()
    {
        foreach (var device in Devices)
        {
            var peak = _deviceClient.GetPeakVolume(device.Id);
            device.Volume = (int)(peak * 100);
        }
    }

    [RelayCommand]
    private void Apply()
    {
        Applied = true;
        // Close the MaterialDesign DialogHost with "true" result
        DialogHost.CloseDialogCommand.Execute(true, null);
    }

    public void Dispose()
    {
        _volumeTimer.Stop();
        _deviceClient.StopMonitoring();
        GC.SuppressFinalize(this);
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
