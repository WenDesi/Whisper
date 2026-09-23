using WhisperDesk.Services;
using Xunit;

namespace WhisperDesk.Tests;

public class StaTaskTests
{
    [Fact]
    public async Task SlowWorkReturnsImmediatelyAndRunsOnASeparateStaThread()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = StaTask.RunAsync(() =>
        {
            started.SetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Test work was not released.");
            return (Thread: Environment.CurrentManagedThreadId, Apartment: Thread.CurrentThread.GetApartmentState());
        });

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(task.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        var result = await task;
        Assert.NotEqual(callerThread, result.Thread);
        Assert.Equal(ApartmentState.STA, result.Apartment);
    }

    [Fact]
    public async Task FailuresArePropagatedToTheCaller()
    {
        var task = StaTask.RunAsync<int>(() => throw new InvalidOperationException("context unavailable"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("context unavailable", error.Message);
    }

    [Fact]
    public async Task CancelledWorkDoesNotRun()
    {
        var ran = false;
        var task = StaTask.RunAsync(() => ran = true, new CancellationToken(canceled: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(ran);
    }
}
