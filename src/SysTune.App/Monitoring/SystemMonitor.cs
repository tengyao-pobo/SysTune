using System.Net.NetworkInformation;
using Microsoft.Win32;
using SysTune.App.Interop;
using SysTune.Core.Logging;
using SysTune.Core.Monitoring;

namespace SysTune.App.Monitoring;

/// <summary>
/// Samples real hardware data on a background task and publishes immutable <see cref="SystemSnapshot"/>s.
/// Each data source is isolated: a source that throws is logged once and reported as unavailable,
/// never taking down the others. Sampling pauses entirely while the widget is hidden.
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    private readonly ILog _log;
    private readonly CpuUsageCalculator _cpu = new();
    private readonly RateCalculator _netDown = new();
    private readonly RateCalculator _netUp = new();
    private readonly HashSet<string> _reportedFailures = [];
    private readonly Dictionary<string, (long Ticks, int Count)> _timings = [];
    private readonly List<KeyValuePair<string, double>> _scratch = new(256);
    private readonly HardwareSensors _sensors;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly AutoResetEvent _wake = new(false);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _intervalMs = 1000;
    private long _tick;

    // Lazily initialised sources
    private PdhQuery? _mainQuery;
    private PdhQuery.PdhCounter? _cpuPerf, _diskIdle, _diskRead, _diskWrite;
    private PdhQuery? _gpuQuery;
    private PdhQuery.PdhCounter? _gpuEngine, _gpuDedicated, _gpuShared;
    private PdhQuery? _thermalQuery;
    private PdhQuery.PdhCounter? _thermal;
    private List<Dxgi.AdapterInfo> _adapters = [];
    private NetworkInterface[] _interfaces = [];
    private TimeSpan? _interfacesRefreshedAt;
    private volatile bool _interfacesDirty = true;
    private IReadOnlyList<DriveSnapshot> _drives = [];
    private TimeSpan? _drivesRefreshedAt;
    private PowerSnapshot? _power;
    private double? _acpiTemp;
    private GpuSnapshot? _lastGpu;
    private string _cpuName = "CPU";
    private double? _cpuBaseMhz;

    public SystemMonitor(ILog log)
    {
        _log = log;
        _sensors = new HardwareSensors(log);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    public event EventHandler<SystemSnapshot>? SnapshotReady;

    public bool EnableGpu { get; set; } = true;

    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            var next = Math.Clamp(value, 250, 10_000);
            var faster = next < _intervalMs;
            _intervalMs = next;
            // Don't sit out a long wait (e.g. 5 s while hidden) after switching to a faster rate.
            if (faster) _wake.Set();
        }
    }

    /// <summary>Pauses sampling (e.g. widget hidden). Resuming takes effect immediately.</summary>
    public bool IsPaused
    {
        get => !_resume.IsSet;
        set
        {
            if (value) _resume.Reset();
            else _resume.Set();
        }
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Thread.CurrentThread.Name = "SysTune.Monitor";
        Initialize();
        while (!ct.IsCancellationRequested)
        {
            if (!_resume.IsSet)
            {
                try
                {
                    _resume.Wait(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                // Discard stale deltas after a pause.
                _netDown.Reset();
                _netUp.Reset();
            }

            var started = _clock.Elapsed;
            try
            {
                var snapshot = Sample();
                SnapshotReady?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                ReportOnce("sample", ex);
            }

            var elapsed = _clock.Elapsed - started;
            var delay = TimeSpan.FromMilliseconds(_intervalMs) - elapsed;
            if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
            if (WaitHandle.WaitAny([ct.WaitHandle, _wake], delay) == 0) return;
        }
    }

    private void Initialize()
    {
        Try("cpu-name", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _cpuName = CleanCpuName(key?.GetValue("ProcessorNameString") as string ?? "CPU");
            if (key?.GetValue("~MHz") is int mhz && mhz > 0) _cpuBaseMhz = mhz;
        });

        Try("pdh-main", () =>
        {
            _mainQuery = PdhQuery.TryOpen();
            if (_mainQuery is null) return;
            _cpuPerf = _mainQuery.TryAdd(@"\Processor Information(_Total)\% Processor Performance");
            _diskIdle = _mainQuery.TryAdd(@"\PhysicalDisk(_Total)\% Idle Time");
            _diskRead = _mainQuery.TryAdd(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _diskWrite = _mainQuery.TryAdd(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _mainQuery.Collect();
        });

        Try("gpu-init", () =>
        {
            _adapters = Dxgi.GetHardwareAdapters();
            _log.Info("GPU adapters: " + (_adapters.Count == 0 ? "(none)" : string.Join(", ", _adapters.Select(a => $"{a.Name} [{a.DedicatedVideoMemory / 1048576} MB]"))));
            if (_adapters.Count == 0) return;
            _gpuQuery = PdhQuery.TryOpen();
            if (_gpuQuery is null) return;
            _gpuEngine = _gpuQuery.TryAdd(@"\GPU Engine(*)\Utilization Percentage");
            _gpuDedicated = _gpuQuery.TryAdd(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _gpuShared = _gpuQuery.TryAdd(@"\GPU Adapter Memory(*)\Shared Usage");
            _gpuQuery.Collect();
        });

        Try("thermal-init", () =>
        {
            _thermalQuery = PdhQuery.TryOpen();
            _thermal = _thermalQuery?.TryAdd(@"\Thermal Zone Information(*)\Temperature");
        });

        // LibreHardwareMonitor can take a second or two to open; keep it off the first sample.
        _ = Task.Run(() => Try("sensors-init", _sensors.Update));
    }

    private SystemSnapshot Sample()
    {
        var now = _clock.Elapsed;
        _tick++;

        // ---- CPU ----
        double? cpuUsage = null;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            cpuUsage = _cpu.Update(idle, kernel, user);
        }

        double? freq = null, diskActive = null;
        double diskRead = 0, diskWrite = 0;
        var pdhStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_mainQuery is not null && _mainQuery.Collect())
        {
            if (_cpuPerf?.Read() is { } perf && _cpuBaseMhz is { } baseMhz) freq = baseMhz * perf / 100.0;
            if (_diskIdle?.Read() is { } idlePct) diskActive = Math.Clamp(100 - idlePct, 0, 100);
            diskRead = _diskRead?.Read() ?? 0;
            diskWrite = _diskWrite?.Read() ?? 0;
        }
        Record("pdh-main", pdhStart);

        // ---- Temperatures (every ~2 s) ----
        if (_tick % Math.Max(1, 2000 / _intervalMs) == 0)
        {
            Try("sensors", _sensors.Update);
            Try("thermal", () =>
            {
                if (_thermal is not null && _thermalQuery!.Collect() && _thermal.ReadArray(_scratch) && _scratch.Count > 0)
                {
                    // Kelvin → Celsius; ignore implausible values reported by some firmware.
                    var c = _scratch.Max(k => k.Value) - 273.15;
                    _acpiTemp = c is > 5 and < 120 ? c : null;
                }
            });
        }
        var cpuTemp = _sensors.CpuTemperatureC;
        var tempSource = cpuTemp is not null ? TemperatureSource.Sensor : _acpiTemp is not null ? TemperatureSource.AcpiThermalZone : TemperatureSource.None;
        cpuTemp ??= _acpiTemp;

        var cpu = new CpuSnapshot(_cpuName, Environment.ProcessorCount, cpuUsage, freq, cpuTemp, tempSource);

        // ---- GPU ----
        var gpu = EnableGpu ? TryGet("gpu", SampleGpu) : null;

        // ---- Memory ----
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        var memory = Native.GlobalMemoryStatusEx(ref mem)
            ? new MemorySnapshot(mem.ullTotalPhys, mem.ullAvailPhys, mem.ullTotalPageFile - mem.ullAvailPageFile, mem.ullTotalPageFile)
            : new MemorySnapshot(0, 0, 0, 0);

        // ---- Drives (every 15 s) ----
        if (_drivesRefreshedAt is not { } drivesAt || now - drivesAt > TimeSpan.FromSeconds(15))
        {
            _drives = TryGet("drives", SampleDrives) ?? _drives;
            _drivesRefreshedAt = now;
        }

        // ---- Network ----
        var network = TryGet("network", () => SampleNetwork(now)) ?? new NetworkSnapshot(false, null, 0, 0);

        // ---- Power (every 10 s) ----
        if (_tick % Math.Max(1, 10_000 / _intervalMs) == 0 || _tick == 1)
        {
            _power = TryGet("power", SamplePower);
        }

        return new SystemSnapshot(
            DateTimeOffset.Now,
            cpu,
            gpu,
            memory,
            _drives,
            _mainQuery is null ? null : new DiskActivitySnapshot(diskActive, diskRead, diskWrite),
            network,
            _power,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private GpuSnapshot? SampleGpu()
    {
        if (_adapters.Count == 0) return null;
        // GPU Engine enumeration is the most expensive counter; sample it at most once per second.
        if (_lastGpu is not null && _intervalMs < 1000 && _tick % 2 == 0) return _lastGpu with { TemperatureC = GpuTemp(_lastGpu.Name) };

        var primary = _adapters.MaxBy(a => a.DedicatedVideoMemory)!;
        double? usage = null;
        ulong? dedicatedUsed = null, sharedUsed = null;
        if (_gpuQuery is not null && _gpuQuery.Collect())
        {
            if (_gpuEngine is not null && _gpuEngine.ReadArray(_scratch))
            {
                var byLuid = GpuEngineAggregator.Aggregate(_scratch);
                usage = byLuid.GetValueOrDefault(primary.Luid);
            }
            if (_gpuDedicated is not null && _gpuDedicated.ReadArray(_scratch))
            {
                var sums = GpuEngineAggregator.SumByLuid(_scratch);
                if (sums.TryGetValue(primary.Luid, out var d)) dedicatedUsed = (ulong)d;
            }
            if (_gpuShared is not null && _gpuShared.ReadArray(_scratch))
            {
                var sums = GpuEngineAggregator.SumByLuid(_scratch);
                if (sums.TryGetValue(primary.Luid, out var s)) sharedUsed = (ulong)s;
            }
        }

        _lastGpu = new GpuSnapshot(
            primary.Name,
            usage,
            dedicatedUsed,
            primary.DedicatedVideoMemory > 0 ? primary.DedicatedVideoMemory : null,
            sharedUsed,
            GpuTemp(primary.Name),
            _adapters.Count);
        return _lastGpu;
    }

    private double? GpuTemp(string adapterName)
    {
        foreach (var (name, temp) in _sensors.GpuTemperatures)
        {
            if (adapterName.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(adapterName, StringComparison.OrdinalIgnoreCase)) return temp;
        }
        // Single discrete GPU reported by the sensor library: assume it is the primary adapter.
        return _sensors.GpuTemperatures.Count == 1 && _adapters.Count == 1 ? _sensors.GpuTemperatures.Values.First() : null;
    }

    private static List<DriveSnapshot> SampleDrives()
    {
        var list = new List<DriveSnapshot>();
        foreach (var d in DriveInfo.GetDrives())
        {
            // Network/optical drives can block for seconds; only local fixed/removable disks are shown.
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!d.IsReady || d.TotalSize <= 0) continue;
                list.Add(new DriveSnapshot(d.Name.TrimEnd('\\'), d.VolumeLabel, d.DriveFormat, (ulong)d.TotalSize, (ulong)d.TotalFreeSpace));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return list;
    }

    private NetworkSnapshot SampleNetwork(TimeSpan now)
    {
        if (_interfacesDirty || _interfacesRefreshedAt is not { } ifAt || now - ifAt > TimeSpan.FromSeconds(30))
        {
            _interfaces = SelectInterfaces(NetworkInterface.GetAllNetworkInterfaces());
            _interfacesRefreshedAt = now;
            _interfacesDirty = false;
            _netDown.Reset();
            _netUp.Reset();
        }

        ulong rx = 0, tx = 0;
        string? primary = null;
        long best = -1;
        foreach (var n in _interfaces)
        {
            try
            {
                var s = n.GetIPStatistics();
                rx += (ulong)Math.Max(0, s.BytesReceived);
                tx += (ulong)Math.Max(0, s.BytesSent);
                var score = s.BytesReceived;
                if (score > best)
                {
                    best = score;
                    primary = n.Name;
                }
            }
            catch (NetworkInformationException)
            {
                _interfacesDirty = true;
            }
        }
        // The interface list is refreshed on NetworkChange events, so no per-tick re-enumeration is needed
        // (NetworkInterface.GetIsNetworkAvailable() enumerates every adapter and was the most expensive call).
        var connected = _interfaces.Length > 0;
        return new NetworkSnapshot(connected, primary, _netDown.Update(rx, now), _netUp.Update(tx, now));
    }

    /// <summary>
    /// Picks the interfaces whose traffic should be counted. Description-based filtering is unreliable
    /// (VM NICs are "Hyper-V Network Adapter"; a Hyper-V host routes through "vEthernet"), so the rule is:
    /// up, not loopback/tunnel, and carrying a default gateway. With an external virtual switch the
    /// physical NIC has no IP configuration, so traffic is not counted twice. Falls back to all up
    /// interfaces when none has a gateway (e.g. a local-only network).
    /// </summary>
    private static NetworkInterface[] SelectInterfaces(NetworkInterface[] all)
    {
        var up = all.Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .ToArray();
        var withGateway = up.Where(HasGateway).ToArray();
        return withGateway.Length > 0 ? withGateway : up;
    }

    private static bool HasGateway(NetworkInterface n)
    {
        try
        {
            foreach (var g in n.GetIPProperties().GatewayAddresses)
            {
                if (!g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)) return true;
            }
        }
        catch (NetworkInformationException)
        {
        }
        return false;
    }

    private static PowerSnapshot? SamplePower()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return null;
        // BatteryFlag 128 = no system battery, 255 = unknown.
        var hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        return new PowerSnapshot(
            hasBattery,
            hasBattery && s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null,
            (s.BatteryFlag & 8) != 0,
            s.ACLineStatus == 1);
    }

    internal static string CleanCpuName(string raw)
    {
        var name = raw.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                      .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                      .Replace(" CPU", "", StringComparison.Ordinal);
        var at = name.IndexOf(" @", StringComparison.Ordinal);
        if (at > 0) name = name[..at];
        foreach (var suffix in new[] { " with Radeon Graphics", " w/ Radeon Graphics" })
        {
            name = name.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
        }
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+-Core Processor$", "");
        return System.Text.RegularExpressions.Regex.Replace(name, @"\s{2,}", " ").Trim();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _interfacesDirty = true;

    private void Try(string name, Action action)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            action();
            Record(name, start);
        }
        catch (Exception ex)
        {
            ReportOnce(name, ex);
        }
    }

    private T? TryGet<T>(string name, Func<T> func)
        where T : class?
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = func();
            Record(name, start);
            return result;
        }
        catch (Exception ex)
        {
            ReportOnce(name, ex);
            return null;
        }
    }

    private void Record(string name, long start)
    {
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        lock (_timings)
        {
            // The first call of each source includes JIT/initialisation; keep it out of the steady-state average.
            if (!_timings.TryGetValue(name, out var v)) _timings[name] = (0, 0);
            else _timings[name] = (v.Ticks + ticks, v.Count + 1);
        }
    }

    /// <summary>Average cost per data source (ms), for diagnostics and the CI performance report.</summary>
    public Dictionary<string, double> GetTimings()
    {
        lock (_timings)
        {
            return _timings.Where(k => k.Value.Count > 0)
                .ToDictionary(k => k.Key, k => Math.Round(k.Value.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / k.Value.Count, 3));
        }
    }

    private void ReportOnce(string source, Exception ex)
    {
        lock (_reportedFailures)
        {
            if (_reportedFailures.Add(source)) _log.Warn($"Monitor source '{source}' failed; showing as unavailable", ex);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _cts?.Cancel();
        _resume.Set();
        var stopped = true;
        try
        {
            stopped = _loop?.Wait(TimeSpan.FromSeconds(2)) ?? true;
        }
        catch (AggregateException)
        {
        }
        // If a sample is still running (e.g. a slow sensor call), don't free native handles under it;
        // the process is exiting and the OS reclaims them.
        if (!stopped) return;
        _mainQuery?.Dispose();
        _gpuQuery?.Dispose();
        _thermalQuery?.Dispose();
        _sensors.Dispose();
        _cts?.Dispose();
        _resume.Dispose();
        _wake.Dispose();
    }
}
