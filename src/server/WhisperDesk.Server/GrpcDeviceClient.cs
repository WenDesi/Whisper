using Grpc.Net.Client;
using WhisperDesk.Proto;

namespace WhisperDesk.Server;

/// <summary>
/// Information about a capture device, returned by GrpcDeviceClient.
/// </summary>
public class CaptureDeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
}

/// <summary>
/// gRPC client for device operations (microphone enumeration, volume metering, etc.).
/// </summary>
public class GrpcDeviceClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly DeviceService.DeviceServiceClient _client;

    public GrpcDeviceClient(string address)
    {
        _channel = GrpcChannel.ForAddress(address);
        _client = new DeviceService.DeviceServiceClient(_channel);
    }

    public List<CaptureDeviceInfo> GetCaptureDevices()
    {
        var response = _client.ListCaptureDevices(new ListCaptureDevicesRequest());
        return response.Devices.Select(d => new CaptureDeviceInfo
        {
            Id = d.Id,
            Name = d.Name,
            IsDefault = d.IsDefault
        }).ToList();
    }

    public float GetPeakVolume(string deviceId)
    {
        var response = _client.GetPeakVolume(new GetPeakVolumeRequest { DeviceId = deviceId });
        return response.Peak;
    }

    public void StartMonitoring()
    {
        _client.StartMonitoring(new StartMonitoringRequest());
    }

    public void StopMonitoring()
    {
        _client.StopMonitoring(new StopMonitoringRequest());
    }

    public void SetActiveDevice(string deviceId)
    {
        _client.SetActiveDevice(new SetActiveDeviceRequest { DeviceId = deviceId });
    }

    public void Dispose()
    {
        _channel.Dispose();
        GC.SuppressFinalize(this);
    }
}
