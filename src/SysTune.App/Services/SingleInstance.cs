namespace SysTune.App.Services;

/// <summary>
/// Ensures one instance per user session. A second launch signals the first one to show itself.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SysTune.SingleInstance.6F1C2B7E";
    private const string EventName = @"Local\SysTune.Activate.6F1C2B7E";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        IsFirst = created;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public bool IsFirst { get; }

    public void SignalFirstInstance() => _activate.Set();

    /// <summary>Invokes <paramref name="onActivate"/> (on a thread-pool thread) when another launch signals.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
