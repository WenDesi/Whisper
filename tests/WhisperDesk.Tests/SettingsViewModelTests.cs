using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperDesk.Proto;
using WhisperDesk.Server;
using WhisperDesk.ViewModels;
using Xunit;

namespace WhisperDesk.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public async Task ConstructionDoesNotWaitForDevices()
    {
        var service = new TestDeviceClient();
        using var client = new GrpcDeviceClient(service);
        await using var vm = new SettingsViewModel(client, "", NullLogger.Instance);

        Assert.Equal(0, service.ListCalls);
        Assert.Equal(0, service.StartCalls);
        Assert.True(vm.IsLoading);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadingYieldsUntilDevicesArriveAndKeepsTheSelectedDevice()
    {
        var devices = new TaskCompletionSource<ListCaptureDevicesResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestDeviceClient { List = ct => devices.Task.WaitAsync(ct) };
        using var client = new GrpcDeviceClient(service);
        await using var vm = new SettingsViewModel(client, "second", NullLogger.Instance);

        var loading = vm.InitializeAsync();
        Assert.False(loading.IsCompleted);
        Assert.True(vm.IsLoading);
        Assert.Empty(vm.Devices);

        var response = DeviceList();
        response.Devices.Add(new CaptureDeviceDto { Id = "second", Name = "Second microphone" });
        devices.SetResult(response);
        await loading.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(vm.IsLoading);
        Assert.Equal("second", vm.SelectedDeviceId);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.Equal(1, service.StartCalls);
    }

    [Fact]
    public async Task ClosingDuringEnumerationCancelsWithoutStartingMonitoring()
    {
        var devices = new TaskCompletionSource<ListCaptureDevicesResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestDeviceClient { List = ct => devices.Task.WaitAsync(ct) };
        using var client = new GrpcDeviceClient(service);
        var vm = new SettingsViewModel(client, "", NullLogger.Instance);
        var loading = vm.InitializeAsync();

        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await loading;

        Assert.False(vm.HasError);
        Assert.Equal(0, service.StartCalls);
        Assert.Equal(0, service.StopCalls);
    }

    [Fact]
    public async Task ClosingDuringMonitorStartupWaitsAsynchronouslyThenStopsExactlyOnce()
    {
        var start = new TaskCompletionSource<StartMonitoringResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource<StopMonitoringResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestDeviceClient
        {
            Start = () => start.Task,
            Stop = () =>
            {
                stopEntered.TrySetResult(true);
                return stop.Task;
            }
        };
        using var client = new GrpcDeviceClient(service);
        var vm = new SettingsViewModel(client, "", NullLogger.Instance);
        var loading = vm.InitializeAsync();
        Assert.Equal(1, service.StartCalls);

        var closing = vm.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.Equal(0, service.StopCalls);

        start.SetResult(new StartMonitoringResponse());
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(closing.IsCompleted);
        stop.SetResult(new StopMonitoringResponse());
        await closing.WaitAsync(TimeSpan.FromSeconds(3));
        await loading;
        await vm.DisposeAsync();

        Assert.Equal(1, service.StopCalls);
        Assert.Equal(0, service.PeakCalls);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task SlowVolumeRequestsNeverOverlapAndAreCancelledOnClose()
    {
        var peakEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peak = new TaskCompletionSource<GetPeakVolumeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestDeviceClient
        {
            Peak = ct =>
            {
                peakEntered.TrySetResult(true);
                return peak.Task.WaitAsync(ct);
            }
        };
        using var client = new GrpcDeviceClient(service);
        var vm = new SettingsViewModel(client, "", NullLogger.Instance);
        await vm.InitializeAsync();
        await peakEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(250);

        Assert.Equal(1, service.PeakCalls);
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, service.StopCalls);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task StartupFailureIsVisibleAndStillCleansUpMonitoring()
    {
        var service = new TestDeviceClient
        {
            Start = () => Task.FromException<StartMonitoringResponse>(
                new RpcException(new Status(StatusCode.Unavailable, "device offline")))
        };
        using var client = new GrpcDeviceClient(service);
        var vm = new SettingsViewModel(client, "", NullLogger.Instance);
        await vm.InitializeAsync();

        Assert.True(vm.HasError);
        Assert.Contains("device offline", vm.ErrorMessage);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        await vm.DisposeAsync();
        Assert.Equal(1, service.StopCalls);
    }

    [Fact]
    public async Task NoDevicesDisablesApplyWithoutStartingMonitoring()
    {
        var service = new TestDeviceClient { List = _ => Task.FromResult(new ListCaptureDevicesResponse()) };
        using var client = new GrpcDeviceClient(service);
        await using var vm = new SettingsViewModel(client, "", NullLogger.Instance);
        await vm.InitializeAsync();

        Assert.True(vm.NoDevicesFound);
        Assert.False(vm.IsLoading);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal(0, service.StartCalls);
    }

    [Fact]
    public async Task ApplyingADeviceDoesNotBlockWaitingForItsResponse()
    {
        var applied = new TaskCompletionSource<SetActiveDeviceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestDeviceClient { SetDevice = _ => applied.Task };
        using var client = new GrpcDeviceClient(service);

        var applying = client.SetActiveDeviceAsync("second");
        Assert.False(applying.IsCompleted);
        Assert.Equal("second", service.RequestedDeviceId);
        applied.SetResult(new SetActiveDeviceResponse());
        await applying.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static ListCaptureDevicesResponse DeviceList()
    {
        var response = new ListCaptureDevicesResponse();
        response.Devices.Add(new CaptureDeviceDto { Id = "default", Name = "Default microphone", IsDefault = true });
        return response;
    }

    private sealed class TestDeviceClient : DeviceService.DeviceServiceClient
    {
        public int ListCalls;
        public int StartCalls;
        public int StopCalls;
        public int PeakCalls;
        public string? RequestedDeviceId;

        public Func<CancellationToken, Task<ListCaptureDevicesResponse>> List { get; init; } =
            _ => Task.FromResult(DeviceList());
        public Func<Task<StartMonitoringResponse>> Start { get; init; } =
            () => Task.FromResult(new StartMonitoringResponse());
        public Func<Task<StopMonitoringResponse>> Stop { get; init; } =
            () => Task.FromResult(new StopMonitoringResponse());
        public Func<CancellationToken, Task<GetPeakVolumeResponse>> Peak { get; init; } =
            _ => Task.FromResult(new GetPeakVolumeResponse { Peak = 0.25f });
        public Func<CancellationToken, Task<SetActiveDeviceResponse>> SetDevice { get; init; } =
            _ => Task.FromResult(new SetActiveDeviceResponse());

        public override AsyncUnaryCall<ListCaptureDevicesResponse> ListCaptureDevicesAsync(
            ListCaptureDevicesRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ListCalls);
            return Call(List(cancellationToken));
        }

        public override AsyncUnaryCall<StartMonitoringResponse> StartMonitoringAsync(
            StartMonitoringRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StartCalls);
            return Call(Start());
        }

        public override AsyncUnaryCall<StopMonitoringResponse> StopMonitoringAsync(
            StopMonitoringRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StopCalls);
            return Call(Stop());
        }

        public override AsyncUnaryCall<GetPeakVolumeResponse> GetPeakVolumeAsync(
            GetPeakVolumeRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PeakCalls);
            return Call(Peak(cancellationToken));
        }

        public override AsyncUnaryCall<SetActiveDeviceResponse> SetActiveDeviceAsync(
            SetActiveDeviceRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            RequestedDeviceId = request.DeviceId;
            return Call(SetDevice(cancellationToken));
        }

        private static AsyncUnaryCall<T> Call<T>(Task<T> response) =>
            new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
