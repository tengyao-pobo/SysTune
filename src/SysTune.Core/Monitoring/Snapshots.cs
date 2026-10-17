namespace SysTune.Core.Monitoring;

/// <summary>Immutable point-in-time view of the machine. Every optional value is <c>null</c> when the
/// platform could not provide it; the UI must show it as unavailable instead of inventing a value.</summary>
public sealed record SystemSnapshot(
    DateTimeOffset Timestamp,
    CpuSnapshot Cpu,
    GpuSnapshot? Gpu,
    MemorySnapshot Memory,
    IReadOnlyList<DriveSnapshot> Drives,
    DiskActivitySnapshot? DiskActivity,
    NetworkSnapshot Network,
    PowerSnapshot? Power,
    TimeSpan Uptime);

public sealed record CpuSnapshot(
    string Name,
    int LogicalProcessors,
    double? UsagePercent,
    double? FrequencyMhz,
    double? TemperatureC,
    TemperatureSource TemperatureSource);

public enum TemperatureSource
{
    None,

    /// <summary>Package/core sensor read by LibreHardwareMonitor (needs elevation + driver).</summary>
    Sensor,

    /// <summary>ACPI thermal zone exposed by Windows performance counters; may not be the CPU die.</summary>
    AcpiThermalZone,
}

public sealed record GpuSnapshot(
    string Name,
    double? UsagePercent,
    ulong? DedicatedUsedBytes,
    ulong? DedicatedTotalBytes,
    ulong? SharedUsedBytes,
    double? TemperatureC,
    int AdapterCount);

public sealed record MemorySnapshot(ulong TotalBytes, ulong AvailableBytes, ulong CommitUsedBytes, ulong CommitLimitBytes)
{
    public ulong UsedBytes => TotalBytes > AvailableBytes ? TotalBytes - AvailableBytes : 0;

    public double UsedPercent => TotalBytes == 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

public sealed record DriveSnapshot(string Name, string Label, string Format, ulong TotalBytes, ulong FreeBytes)
{
    public ulong UsedBytes => TotalBytes > FreeBytes ? TotalBytes - FreeBytes : 0;

    public double UsedPercent => TotalBytes == 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

public sealed record DiskActivitySnapshot(double? ActivePercent, double ReadBytesPerSecond, double WriteBytesPerSecond);

public sealed record NetworkSnapshot(bool IsConnected, string? PrimaryAdapter, double DownloadBytesPerSecond, double UploadBytesPerSecond);

public sealed record PowerSnapshot(bool HasBattery, int? BatteryPercent, bool IsCharging, bool OnAcPower);
