namespace WhisperDesk.Services;

public static class StaTask
{
    public static Task<T> RunAsync<T>(Func<T> action, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<T>(ct);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = action();
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WhisperDesk context capture"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
