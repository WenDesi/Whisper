using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using WhisperDesk.Server;

namespace WhisperDesk.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly GrpcDeviceClient _deviceClient;
    private readonly DispatcherTimer _volumeTimer;

    public ObservableCollection<MicrophoneItem> Devices { get; } = new();

    [ObservableProperty]
    private bool _noDevicesFound;

    public string? SelectedDeviceId => Devices.FirstOrDefault(d => d.IsSelected)?.Id;

    public bool Applied { get; private set; }

    public SettingsViewModel(GrpcDeviceClient deviceClient, string currentDeviceId)
    {
        _deviceClient = deviceClient;

        LoadDevices(currentDeviceId);

        _deviceClient.StartMonitoring();

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
        DialogHost.CloseDialogCommand.Execute(true, null);
    }

    public void Dispose()
    {
        _volumeTimer.Stop();
        _deviceClient.StopMonitoring();
        GC.SuppressFinalize(this);
    }
}

public partial class MicrophoneItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _volume;
}
