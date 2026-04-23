using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperDesk.Services;

/// <summary>
/// Information about an audio capture device.
/// </summary>
public class AudioDeviceInfo
{
    /// <summary>WASAPI MMDevice.ID, stable across reboots.</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable device name (MMDevice.FriendlyName).</summary>
    public string Name { get; init; } = "";

    /// <summary>Whether this is the system default communications capture device.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// Enumerates audio capture devices and reads real-time volume levels via WASAPI.
/// </summary>
public class AudioDeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<string, MMDevice> _openDevices = new();
    private readonly List<WasapiCapture> _monitorCaptures = new();

    /// <summary>Get all active capture (microphone) devices.</summary>
    public List<AudioDeviceInfo> GetCaptureDevices()
    {
        string? defaultId = null;
        try
        {
            var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            defaultId = defaultDevice.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No default capture device available
        }

        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        return devices.Select(d => new AudioDeviceInfo
        {
            Id = d.ID,
            Name = d.FriendlyName,
            IsDefault = defaultId != null && d.ID == defaultId
        }).ToList();
    }

    /// <summary>
    /// Get the current peak volume (0.0-1.0) for a device by its WASAPI ID.
    /// Reads from the Windows audio mixer -- zero overhead, no recording needed.
    /// Keeps MMDevice handles open for accurate continuous reads.
    /// Returns 0 if device not found or on error.
    /// </summary>
    public float GetPeakVolume(string deviceId)
    {
        try
        {
            if (!_openDevices.TryGetValue(deviceId, out var device))
            {
                device = _enumerator.GetDevice(deviceId);
                _openDevices[deviceId] = device;
            }
            return device.AudioMeterInformation.MasterPeakValue;
        }
        catch
        {
            _openDevices.Remove(deviceId);
            return 0f;
        }
    }

    /// <summary>
    /// Start silent capture streams on all devices so that MasterPeakValue
    /// returns live data. Some drivers only report peak values when an active
    /// audio stream exists on the device.
    /// Call StopMonitoring() when volume display is no longer needed.
    /// </summary>
    public void StartMonitoring()
    {
        StopMonitoring();

        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in devices)
        {
            try
            {
                var capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
                capture.DataAvailable += (_, _) => { }; // discard audio data
                capture.StartRecording();
                _monitorCaptures.Add(capture);
            }
            catch
            {
                // Device may not support shared mode capture — skip it
            }
        }
    }

    /// <summary>
    /// Stop all monitoring capture streams.
    /// </summary>
    public void StopMonitoring()
    {
        foreach (var capture in _monitorCaptures)
        {
            try
            {
                capture.StopRecording();
                capture.Dispose();
            }
            catch { }
        }
        _monitorCaptures.Clear();
    }

    /// <summary>
    /// Release cached device handles. Call when volume monitoring is no longer needed.
    /// </summary>
    public void ReleaseCachedDevices()
    {
        foreach (var device in _openDevices.Values)
        {
            try { device.Dispose(); } catch { }
        }
        _openDevices.Clear();
    }

    /// <summary>
    /// Resolve a WASAPI device ID to a WaveIn device number.
    /// WaveIn uses a separate device numbering system, so we match by name.
    /// Returns 0 (default device) if the ID cannot be resolved.
    /// </summary>
    public int ResolveWaveInDeviceNumber(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return 0;

        try
        {
            using var mmDevice = _enumerator.GetDevice(deviceId);
            var targetName = mmDevice.FriendlyName;

            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                // WaveIn ProductName is truncated to 31 chars
                if (targetName.StartsWith(caps.ProductName.TrimEnd('\0')))
                    return i;
            }
        }
        catch
        {
            // Device may have been disconnected
        }

        return 0; // fallback to default
    }

    public void Dispose()
    {
        StopMonitoring();
        ReleaseCachedDevices();
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
