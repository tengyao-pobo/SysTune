using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SysTune.Core.Formatting;
using SysTune.Core.Monitoring;

namespace SysTune.App.ViewModels;

/// <summary>Presentation state for the system metrics. Updated on the UI thread from snapshots.</summary>
public sealed partial class SystemViewModel : ObservableObject
{
    public const int HistoryLength = 60;
    private readonly SampleHistory _cpuHistory = new(HistoryLength);
    private readonly SampleHistory _gpuHistory = new(HistoryLength);
    private readonly SampleHistory _memHistory = new(HistoryLength);
    private readonly SampleHistory _downHistory = new(HistoryLength);
    private readonly SampleHistory _upHistory = new(HistoryLength);

    public bool UseFahrenheit { get; set; }

    // ---- CPU ----
    [ObservableProperty] public partial string CpuName { get; set; } = "CPU";

    [ObservableProperty] public partial double CpuUsage { get; set; }

    [ObservableProperty] public partial string CpuUsageText { get; set; } = "—";

    [ObservableProperty] public partial string CpuTempText { get; set; } = "";

    [ObservableProperty] public partial string CpuTempTooltip { get; set; } = "";

    [ObservableProperty] public partial string CpuDetailText { get; set; } = "";

    [ObservableProperty] public partial double[] CpuHistory { get; set; } = [];

    // ---- GPU ----
    [ObservableProperty] public partial bool HasGpu { get; set; }

    [ObservableProperty] public partial string GpuName { get; set; } = "GPU";

    [ObservableProperty] public partial double GpuUsage { get; set; }

    [ObservableProperty] public partial string GpuUsageText { get; set; } = "—";

    [ObservableProperty] public partial string GpuTempText { get; set; } = "";

    [ObservableProperty] public partial string GpuDetailText { get; set; } = "";

    [ObservableProperty] public partial double[] GpuHistory { get; set; } = [];

    // ---- Memory ----
    [ObservableProperty] public partial double MemPercent { get; set; }

    [ObservableProperty] public partial string MemPercentText { get; set; } = "—";

    [ObservableProperty] public partial string MemDetailText { get; set; } = "";

    [ObservableProperty] public partial string MemTooltip { get; set; } = "";

    [ObservableProperty] public partial double[] MemHistory { get; set; } = [];

    // ---- Network ----
    [ObservableProperty] public partial string NetDownText { get; set; } = "—";

    [ObservableProperty] public partial string NetUpText { get; set; } = "—";

    [ObservableProperty] public partial string NetDownShort { get; set; } = "—";

    [ObservableProperty] public partial string NetUpShort { get; set; } = "—";

    [ObservableProperty] public partial string NetAdapterText { get; set; } = "";

    [ObservableProperty] public partial bool NetConnected { get; set; } = true;

    [ObservableProperty] public partial double[] NetDownHistory { get; set; } = [];

    [ObservableProperty] public partial double[] NetUpHistory { get; set; } = [];

    // ---- Storage ----
    public ObservableCollection<DriveItem> Drives { get; } = [];

    [ObservableProperty] public partial string DiskActivityText { get; set; } = "";

    // ---- Footer ----
    [ObservableProperty] public partial string UptimeText { get; set; } = "";

    [ObservableProperty] public partial bool HasBattery { get; set; }

    [ObservableProperty] public partial string BatteryText { get; set; } = "";

    [ObservableProperty] public partial string BatteryGlyph { get; set; } = "";

    /// <summary>Compact one-line summary for the tray tooltip.</summary>
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public void Apply(SystemSnapshot s)
    {
        // CPU
        CpuName = s.Cpu.Name;
        if (s.Cpu.UsagePercent is { } cpu)
        {
            CpuUsage = cpu;
            CpuUsageText = Fmt.Percent(cpu);
            _cpuHistory.Add(cpu);
            CpuHistory = _cpuHistory.ToArray();
        }
        CpuTempText = s.Cpu.TemperatureC is null ? "" : Fmt.Temperature(s.Cpu.TemperatureC, UseFahrenheit);
        CpuTempTooltip = s.Cpu.TemperatureSource switch
        {
            TemperatureSource.Sensor => "CPU 封裝溫度（感測器）",
            TemperatureSource.AcpiThermalZone => "ACPI 熱區溫度（由主機板韌體提供，可能不等於 CPU 核心溫度）",
            _ => "此系統未提供 CPU 溫度（需系統管理員權限與相容的感測器）",
        };
        var threads = s.Cpu.LogicalProcessors.ToString(CultureInfo.InvariantCulture) + " 執行緒";
        CpuDetailText = s.Cpu.FrequencyMhz is { } mhz ? $"{Fmt.Frequency(mhz)} · {threads}" : threads;

        // GPU
        HasGpu = s.Gpu is not null;
        if (s.Gpu is { } g)
        {
            GpuName = g.AdapterCount > 1 ? $"{g.Name}（共 {g.AdapterCount} 張）" : g.Name;
            GpuUsage = g.UsagePercent ?? 0;
            GpuUsageText = Fmt.Percent(g.UsagePercent);
            GpuTempText = g.TemperatureC is null ? "" : Fmt.Temperature(g.TemperatureC, UseFahrenheit);
            if (g.DedicatedTotalBytes is { } total && total >= 512UL * 1024 * 1024 && g.DedicatedUsedBytes is { } used)
            {
                GpuDetailText = $"VRAM {Fmt.Bytes(used)} / {Fmt.Bytes(total)}";
            }
            else if (g.SharedUsedBytes is { } shared)
            {
                GpuDetailText = $"共用記憶體 {Fmt.Bytes(shared)}";
            }
            else
            {
                GpuDetailText = "";
            }
            _gpuHistory.Add(g.UsagePercent ?? 0);
            GpuHistory = _gpuHistory.ToArray();
        }

        // Memory
        var m = s.Memory;
        MemPercent = m.UsedPercent;
        MemPercentText = m.TotalBytes == 0 ? "—" : Fmt.Percent(m.UsedPercent);
        MemDetailText = m.TotalBytes == 0 ? "" : $"{Fmt.Bytes(m.UsedBytes)} / {Fmt.Bytes(m.TotalBytes)}";
        MemTooltip = m.CommitLimitBytes == 0 ? "" : $"已認可 {Fmt.Bytes(m.CommitUsedBytes)} / {Fmt.Bytes(m.CommitLimitBytes)}\n可用 {Fmt.Bytes(m.AvailableBytes)}";
        _memHistory.Add(m.UsedPercent);
        MemHistory = _memHistory.ToArray();

        // Network
        var n = s.Network;
        NetConnected = n.IsConnected;
        NetDownText = Fmt.Rate(n.DownloadBytesPerSecond);
        NetUpText = Fmt.Rate(n.UploadBytesPerSecond);
        NetDownShort = ShortRate(n.DownloadBytesPerSecond);
        NetUpShort = ShortRate(n.UploadBytesPerSecond);
        NetAdapterText = n.IsConnected ? n.PrimaryAdapter ?? "" : "未連線";
        _downHistory.Add(n.DownloadBytesPerSecond);
        _upHistory.Add(n.UploadBytesPerSecond);
        NetDownHistory = _downHistory.ToArray();
        NetUpHistory = _upHistory.ToArray();

        // Storage
        SyncDrives(s.Drives);
        DiskActivityText = s.DiskActivity is { } d
            ? $"使用中 {Fmt.Percent(d.ActivePercent)} · 讀 {Fmt.Rate(d.ReadBytesPerSecond)} · 寫 {Fmt.Rate(d.WriteBytesPerSecond)}"
            : "";

        // Footer
        UptimeText = "已開機 " + Fmt.Uptime(s.Uptime);
        HasBattery = s.Power is { HasBattery: true, BatteryPercent: not null };
        if (s.Power is { HasBattery: true, BatteryPercent: { } pct } p)
        {
            BatteryText = pct.ToString(CultureInfo.InvariantCulture) + "%" + (p.IsCharging ? " 充電中" : p.OnAcPower ? " 已接上電源" : "");
            BatteryGlyph = p.IsCharging ? "" : "";
        }

        Summary = $"CPU {CpuUsageText} · RAM {MemPercentText}" + (HasGpu ? $" · GPU {GpuUsageText}" : "") + $"\n↓ {NetDownText}  ↑ {NetUpText}";
    }

    /// <summary>Very short rate for the compact pill, e.g. "1.2M", "640K".</summary>
    internal static string ShortRate(double bps)
    {
        if (bps < 1024) return bps.ToString("F0", CultureInfo.InvariantCulture) + "B";
        if (bps < 1024 * 1000) return (bps / 1024).ToString(bps < 10 * 1024 ? "F1" : "F0", CultureInfo.InvariantCulture) + "K";
        var mb = bps / (1024 * 1024);
        return mb.ToString(mb < 10 ? "F1" : "F0", CultureInfo.InvariantCulture) + "M";
    }

    private void SyncDrives(IReadOnlyList<DriveSnapshot> drives)
    {
        var shown = drives.Take(4).ToList();
        while (Drives.Count > shown.Count) Drives.RemoveAt(Drives.Count - 1);
        for (var i = 0; i < shown.Count; i++)
        {
            if (i >= Drives.Count) Drives.Add(new DriveItem());
            Drives[i].Update(shown[i]);
        }
    }
}

public sealed partial class DriveItem : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty] public partial string Tooltip { get; set; } = "";

    [ObservableProperty] public partial double UsedPercent { get; set; }

    [ObservableProperty] public partial string Text { get; set; } = "";

    public void Update(DriveSnapshot d)
    {
        Name = d.Name;
        UsedPercent = d.UsedPercent;
        Text = $"{Fmt.Bytes(d.UsedBytes, 0)} / {Fmt.Bytes(d.TotalBytes, 0)}";
        Tooltip = $"{(string.IsNullOrWhiteSpace(d.Label) ? "本機磁碟" : d.Label)} ({d.Name}) · {d.Format}\n可用 {Fmt.Bytes(d.FreeBytes)}（{Fmt.Percent(100 - d.UsedPercent)}）";
    }
}
