namespace SysTune.Core.Common;

/// <summary>
/// Exponential backoff with jitter, capped at <see cref="Max"/>. Used for network retries so that a
/// flaky connection or an outage never results in tight retry loops.
/// </summary>
public sealed class Backoff
{
    private readonly Random _random;
    private int _attempt;

    public Backoff(TimeSpan initial, TimeSpan max, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initial, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, initial);
        Initial = initial;
        Max = max;
        _random = random ?? Random.Shared;
    }

    public TimeSpan Initial { get; }

    public TimeSpan Max { get; }

    public int Attempt => _attempt;

    /// <summary>Returns the next delay and advances the attempt counter.</summary>
    public TimeSpan Next()
    {
        var exp = Math.Min(_attempt, 20);
        _attempt++;
        var baseMs = Math.Min(Max.TotalMilliseconds, Initial.TotalMilliseconds * Math.Pow(2, exp));
        // +/- 20 % jitter, never exceeding Max.
        var jitter = 1 + (_random.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromMilliseconds(Math.Min(Max.TotalMilliseconds, baseMs * jitter));
    }

    public void Reset() => _attempt = 0;
}
