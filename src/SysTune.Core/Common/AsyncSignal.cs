namespace SysTune.Core.Common;

/// <summary>Auto-reset signal that can be awaited with a timeout. Used to wake polling loops early.</summary>
public sealed class AsyncSignal
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _tcs = NewTcs();

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set()
    {
        lock (_gate) _tcs.TrySetResult();
    }

    /// <summary>Waits until <see cref="Set"/> is called or the timeout elapses. Returns true if signalled.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task task;
        lock (_gate) task = _tcs.Task;
        var signalled = false;
        try
        {
            if (timeout == Timeout.InfiniteTimeSpan) await task.WaitAsync(ct).ConfigureAwait(false);
            else await task.WaitAsync(timeout, ct).ConfigureAwait(false);
            signalled = true;
        }
        catch (TimeoutException)
        {
        }
        lock (_gate)
        {
            if (_tcs.Task.IsCompleted) _tcs = NewTcs();
        }
        return signalled;
    }
}
