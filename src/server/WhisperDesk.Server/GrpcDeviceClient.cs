using Grpc.Net.Client;
using WhisperDesk.Proto;

namespace WhisperDesk.Server;

/// <summary>
/// gRPC client for device operations (microphone enumeration, volume metering, etc.).
/// </summary>
public class GrpcDeviceClient : IDisposable
{
    private readonly GrpcChannel? _channel;
    private readonly DeviceService.DeviceServiceClient _client;

    public GrpcDeviceClient(string address)
    {
        _channel = GrpcChannel.ForAddress(address);
        _client = new DeviceService.DeviceServiceClient(_channel);
    }

    public GrpcDeviceClient(DeviceService.DeviceServiceClient client)
    {
        _client = client;
    }

    public async Task<List<CaptureDeviceInfo>> GetCaptureDevicesAsync(CancellationToken ct = default)
    {
        using var call = _client.ListCaptureDevicesAsync(new ListCaptureDevicesRequest(), cancellationToken: ct);
        var response = await call.ResponseAsync.ConfigureAwait(false);
        return response.Devices.Select(d => new CaptureDeviceInfo
        {
            Id = d.Id,
            Name = d.Name,
            IsDefault = d.IsDefault
        }).ToList();
    }

    public async Task<float> GetPeakVolumeAsync(string deviceId, CancellationToken ct = default)
    {
        using var call = _client.GetPeakVolumeAsync(new GetPeakVolumeRequest { DeviceId = deviceId }, cancellationToken: ct);
        var response = await call.ResponseAsync.ConfigureAwait(false);
        return response.Peak;
    }

    public async Task StartMonitoringAsync(CancellationToken ct = default)
    {
        using var call = _client.StartMonitoringAsync(new StartMonitoringRequest(), cancellationToken: ct);
        await call.ResponseAsync.ConfigureAwait(false);
    }

    public async Task StopMonitoringAsync(CancellationToken ct = default)
    {
        using var call = _client.StopMonitoringAsync(new StopMonitoringRequest(), cancellationToken: ct);
        await call.ResponseAsync.ConfigureAwait(false);
    }

    public async Task SetActiveDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        using var call = _client.SetActiveDeviceAsync(new SetActiveDeviceRequest { DeviceId = deviceId }, cancellationToken: ct);
        await call.ResponseAsync.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _channel?.Dispose();
        GC.SuppressFinalize(this);
    }
}
