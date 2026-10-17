using System.Globalization;

namespace SysTune.Core.Formatting;

/// <summary>Human-readable formatting helpers. All output uses invariant culture digits.</summary>
public static class Fmt
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] RateUnits = ["B/s", "KB/s", "MB/s", "GB/s"];

    /// <summary>Formats a byte count using binary (1024) multiples, e.g. "15.8 GB".</summary>
    public static string Bytes(double bytes, int decimals = 1)
    {
        if (double.IsNaN(bytes) || bytes < 0) return "—";
        var unit = 0;
        while (bytes >= 1024 && unit < ByteUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        var d = unit == 0 ? 0 : decimals;
        return bytes.ToString("F" + d, CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
    }

    /// <summary>Formats a transfer rate in bytes per second, keeping at most 3 significant digits.</summary>
    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0) return "—";
        var unit = 0;
        var v = bytesPerSecond;
        while (v >= 1000 && unit < RateUnits.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        var format = unit == 0 || v >= 100 ? "F0" : v >= 10 ? "F1" : "F2";
        return v.ToString(format, CultureInfo.InvariantCulture) + " " + RateUnits[unit];
    }

    /// <summary>Formats a 0-100 percentage with no decimals.</summary>
    public static string Percent(double? value)
        => value is null || double.IsNaN(value.Value) ? "—" : Math.Round(Math.Clamp(value.Value, 0, 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Formats a temperature, optionally converting to Fahrenheit.</summary>
    public static string Temperature(double? celsius, bool fahrenheit = false)
    {
        if (celsius is null || double.IsNaN(celsius.Value)) return "—";
        var v = fahrenheit ? celsius.Value * 9 / 5 + 32 : celsius.Value;
        return Math.Round(v).ToString(CultureInfo.InvariantCulture) + (fahrenheit ? "°F" : "°C");
    }

    /// <summary>Formats a media position as m:ss or h:mm:ss.</summary>
    public static string MediaTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
    }

    /// <summary>Formats an uptime span compactly in Traditional Chinese, e.g. "3 天 4 小時".</summary>
    public static string Uptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} 天 {t.Hours} 小時";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小時 {t.Minutes} 分";
        return $"{Math.Max(0, (int)t.TotalMinutes)} 分鐘";
    }

    /// <summary>Formats a clock speed in MHz as GHz.</summary>
    public static string Frequency(double? mhz)
        => mhz is null or <= 0 ? "—" : (mhz.Value / 1000).ToString("F2", CultureInfo.InvariantCulture) + " GHz";
}
