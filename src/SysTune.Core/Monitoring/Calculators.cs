namespace SysTune.Core.Monitoring;

/// <summary>
/// Computes overall CPU utilisation from cumulative idle/kernel/user tick counters
/// (the values returned by Win32 <c>GetSystemTimes</c>; kernel time includes idle time).
/// </summary>
public sealed class CpuUsageCalculator
{
    private ulong _idle, _kernel, _user;
    private bool _hasPrevious;

    /// <returns>Usage in percent, or <c>null</c> for the first sample.</returns>
    public double? Update(ulong idle, ulong kernel, ulong user)
    {
        double? result = null;
        if (_hasPrevious && idle >= _idle && kernel >= _kernel && user >= _user)
        {
            var idleDelta = idle - _idle;
            var total = (kernel - _kernel) + (user - _user);
            if (total > 0)
            {
                result = Math.Clamp((1.0 - (double)idleDelta / total) * 100.0, 0, 100);
            }
        }
        _idle = idle;
        _kernel = kernel;
        _user = user;
        _hasPrevious = true;
        return result;
    }
}

/// <summary>
/// Converts a monotonically increasing byte counter into a per-second rate. A counter that goes
/// backwards (adapter reset, interface removed) re-bases instead of reporting a negative/huge value.
/// </summary>
public sealed class RateCalculator
{
    private ulong _last;
    private TimeSpan _lastTime;
    private bool _hasPrevious;

    public double Update(ulong counter, TimeSpan timestamp)
    {
        double rate = 0;
        if (_hasPrevious && counter >= _last && timestamp > _lastTime)
        {
            rate = (counter - _last) / (timestamp - _lastTime).TotalSeconds;
        }
        _last = counter;
        _lastTime = timestamp;
        _hasPrevious = true;
        return rate;
    }

    public void Reset() => _hasPrevious = false;
}

/// <summary>Fixed-capacity ring buffer of samples used for sparklines. Not thread-safe.</summary>
public sealed class SampleHistory
{
    private readonly double[] _buffer;
    private int _start;

    public SampleHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _buffer = new double[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count { get; private set; }

    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        var index = (_start + Count) % _buffer.Length;
        _buffer[index] = value;
        if (Count < _buffer.Length) Count++;
        else _start = (_start + 1) % _buffer.Length;
    }

    /// <summary>Copies samples, oldest first, into <paramref name="destination"/>.</summary>
    public int CopyTo(Span<double> destination)
    {
        var n = Math.Min(Count, destination.Length);
        var skip = Count - n;
        for (var i = 0; i < n; i++)
        {
            destination[i] = _buffer[(_start + skip + i) % _buffer.Length];
        }
        return n;
    }

    public double Max()
    {
        double max = 0;
        for (var i = 0; i < Count; i++) max = Math.Max(max, _buffer[(_start + i) % _buffer.Length]);
        return max;
    }

    public double[] ToArray()
    {
        var a = new double[Count];
        CopyTo(a);
        return a;
    }
}
