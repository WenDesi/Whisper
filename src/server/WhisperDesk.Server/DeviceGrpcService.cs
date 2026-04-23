using Grpc.Core;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Services;
using WhisperDesk.Proto;

namespace WhisperDesk.Server;

public class DeviceGrpcService : DeviceService.DeviceServiceBase
{
    private readonly AudioDeviceService _deviceService;
    private readonly PipelineConfig _pipelineConfig;

    public DeviceGrpcService(AudioDeviceService deviceService, PipelineConfig pipelineConfig)
    {
        _deviceService = deviceService;
        _pipelineConfig = pipelineConfig;
    }

    public override Task<ListCaptureDevicesResponse> ListCaptureDevices(ListCaptureDevicesRequest request, ServerCallContext context)
    {
        var devices = _deviceService.GetCaptureDevices();
        var response = new ListCaptureDevicesResponse();
        foreach (var d in devices)
        {
            response.Devices.Add(new CaptureDeviceDto
            {
                Id = d.Id,
                Name = d.Name,
                IsDefault = d.IsDefault
            });
        }
        return Task.FromResult(response);
    }

    public override Task<GetPeakVolumeResponse> GetPeakVolume(GetPeakVolumeRequest request, ServerCallContext context)
    {
        var peak = _deviceService.GetPeakVolume(request.DeviceId);
        return Task.FromResult(new GetPeakVolumeResponse { Peak = peak });
    }

    public override Task<StartMonitoringResponse> StartMonitoring(StartMonitoringRequest request, ServerCallContext context)
    {
        _deviceService.StartMonitoring();
        return Task.FromResult(new StartMonitoringResponse());
    }

    public override Task<StopMonitoringResponse> StopMonitoring(StopMonitoringRequest request, ServerCallContext context)
    {
        _deviceService.StopMonitoring();
        _deviceService.ReleaseCachedDevices();
        return Task.FromResult(new StopMonitoringResponse());
    }

    public override Task<SetActiveDeviceResponse> SetActiveDevice(SetActiveDeviceRequest request, ServerCallContext context)
    {
        _pipelineConfig.AudioDeviceId = request.DeviceId;
        return Task.FromResult(new SetActiveDeviceResponse());
    }
}
