namespace SysTune.Core.Common;

/// <summary>Abstraction over wall-clock and monotonic time, so time-dependent logic can be tested.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic timestamp suitable for measuring elapsed time.</summary>
    TimeSpan Elapsed { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_start);
}
