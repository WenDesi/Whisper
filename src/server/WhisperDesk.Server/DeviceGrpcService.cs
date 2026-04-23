using Grpc.Core;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Services;
using WhisperDesk.Proto;

namespace WhisperDesk.Server;

public class DeviceGrpcService : DeviceService.DeviceServiceBase
{
    private readonly AudioDeviceService _audioDeviceService;
    private readonly PipelineConfig _pipelineConfig;

    public DeviceGrpcService(AudioDeviceService audioDeviceService, PipelineConfig pipelineConfig)
    {
        _audioDeviceService = audioDeviceService;
        _pipelineConfig = pipelineConfig;
    }

    public override Task<ListCaptureDevicesResponse> ListCaptureDevices(ListCaptureDevicesRequest request, ServerCallContext context)
    {
        var devices = _audioDeviceService.GetCaptureDevices();
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
        var peak = _audioDeviceService.GetPeakVolume(request.DeviceId);
        return Task.FromResult(new GetPeakVolumeResponse { Peak = peak });
    }

    public override Task<StartMonitoringResponse> StartMonitoring(StartMonitoringRequest request, ServerCallContext context)
    {
        _audioDeviceService.StartMonitoring();
        return Task.FromResult(new StartMonitoringResponse());
    }

    public override Task<StopMonitoringResponse> StopMonitoring(StopMonitoringRequest request, ServerCallContext context)
    {
        _audioDeviceService.StopMonitoring();
        return Task.FromResult(new StopMonitoringResponse());
    }

    public override Task<SetActiveDeviceResponse> SetActiveDevice(SetActiveDeviceRequest request, ServerCallContext context)
    {
        _pipelineConfig.AudioDeviceId = request.DeviceId;
        return Task.FromResult(new SetActiveDeviceResponse());
    }
}
