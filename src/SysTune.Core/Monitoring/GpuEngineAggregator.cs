using System.Globalization;
using System.Text.RegularExpressions;

namespace SysTune.Core.Monitoring;

/// <summary>
/// Aggregates the Windows "GPU Engine" performance counter instances into per-adapter utilisation,
/// using the same method as Task Manager: utilisation is summed per (adapter, engine type) and the
/// adapter's load is the busiest engine type.
/// Instance names look like <c>pid_1234_luid_0x00000000_0x0000C5E1_phys_0_eng_0_engtype_3D</c>.
/// </summary>
public static partial class GpuEngineAggregator
{
    [GeneratedRegex(@"luid_0x(?<hi>[0-9a-fA-F]+)_0x(?<lo>[0-9a-fA-F]+)_phys_(?<phys>\d+).*?engtype_(?<type>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EngineRegex();

    [GeneratedRegex(@"luid_0x(?<hi>[0-9a-fA-F]+)_0x(?<lo>[0-9a-fA-F]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LuidRegex();

    /// <summary>Parses the adapter LUID out of a GPU counter instance name.</summary>
    public static long? ParseLuid(string instanceName)
    {
        var m = LuidRegex().Match(instanceName);
        if (!m.Success) return null;
        var hi = uint.Parse(m.Groups["hi"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var lo = uint.Parse(m.Groups["lo"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((long)hi << 32) | lo;
    }

    /// <summary>Returns adapter LUID → utilisation percent (0-100).</summary>
    public static Dictionary<long, double> Aggregate(IEnumerable<KeyValuePair<string, double>> instances)
    {
        var perType = new Dictionary<(long Luid, string Type), double>();
        foreach (var (name, value) in instances)
        {
            if (double.IsNaN(value) || value <= 0) continue;
            var m = EngineRegex().Match(name);
            if (!m.Success) continue;
            var luid = ParseLuid(name)!.Value;
            var key = (luid, m.Groups["type"].Value);
            perType[key] = perType.GetValueOrDefault(key) + value;
        }

        var result = new Dictionary<long, double>();
        foreach (var ((luid, _), value) in perType)
        {
            result[luid] = Math.Min(100, Math.Max(result.GetValueOrDefault(luid), value));
        }
        return result;
    }

    /// <summary>Sums a per-adapter counter (e.g. "GPU Adapter Memory\Dedicated Usage") by LUID.</summary>
    public static Dictionary<long, double> SumByLuid(IEnumerable<KeyValuePair<string, double>> instances)
    {
        var result = new Dictionary<long, double>();
        foreach (var (name, value) in instances)
        {
            if (double.IsNaN(value)) continue;
            if (ParseLuid(name) is { } luid) result[luid] = result.GetValueOrDefault(luid) + value;
        }
        return result;
    }
}
