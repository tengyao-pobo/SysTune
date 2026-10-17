using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using SysTune.Core.Logging;

namespace SysTune.App.Monitoring;

/// <summary>
/// Optional temperature source backed by LibreHardwareMonitorLib.
/// GPU sensors (NVAPI / ADL / Intel) work without elevation. CPU package sensors require
/// administrator rights plus the kernel driver LibreHardwareMonitor uses, so they are only
/// attempted when the process is elevated. Every failure degrades to "unavailable".
/// </summary>
internal sealed class HardwareSensors : IDisposable
{
    private readonly ILog _log;
    private readonly Lock _gate = new();
    private Computer? _computer;
    private bool _failed;

    public HardwareSensors(ILog log) => _log = log;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public double? CpuTemperatureC { get; private set; }

    /// <summary>GPU name → temperature (°C).</summary>
    public IReadOnlyDictionary<string, double> GpuTemperatures { get; private set; } = new Dictionary<string, double>();

    public void Update()
    {
        lock (_gate)
        {
            if (_failed) return;
            try
            {
                if (_computer is null)
                {
                    var elevated = IsElevated;
                    _computer = new Computer { IsGpuEnabled = true, IsCpuEnabled = elevated };
                    _computer.Open();
                    _log.Info($"Hardware sensors opened (elevated={elevated}); hardware: " +
                              string.Join(", ", _computer.Hardware.Select(h => $"{h.HardwareType}:{h.Name}")));
                }

                double? cpu = null;
                var gpus = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            cpu = PickCpuTemperature(hw.Sensors) ?? cpu;
                            break;
                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            var t = PickGpuTemperature(hw.Sensors);
                            if (t is not null) gpus[hw.Name] = t.Value;
                            break;
                    }
                }
                CpuTemperatureC = cpu;
                GpuTemperatures = gpus;
            }
            catch (Exception ex)
            {
                _failed = true;
                _log.Warn("Hardware sensors unavailable; temperatures disabled", ex);
                CloseComputer();
            }
        }
    }

    private static double? PickCpuTemperature(ISensor[] sensors)
    {
        string[] preferred = ["CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core Average", "CPU Cores"];
        foreach (var name in preferred)
        {
            var s = sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Name == name && x.Value is > 0 and < 125);
            if (s is not null) return s.Value;
        }
        var max = sensors.Where(x => x.SensorType == SensorType.Temperature && x.Value is > 0 and < 125).Select(x => (double?)x.Value).Max();
        return max;
    }

    private static double? PickGpuTemperature(ISensor[] sensors)
    {
        var core = sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && x.Value is > 0 and < 125);
        return core?.Value ?? sensors.Where(x => x.SensorType == SensorType.Temperature && x.Value is > 0 and < 125).Select(x => (double?)x.Value).Max();
    }

    private void CloseComputer()
    {
        try
        {
            _computer?.Close();
        }
        catch (Exception)
        {
            // ignore
        }
        _computer = null;
    }

    public void Dispose()
    {
        // Computer.Open() can be slow on some systems; never block app exit on it.
        if (!_gate.TryEnter(TimeSpan.FromSeconds(1))) return;
        try
        {
            CloseComputer();
            _failed = true;
        }
        finally
        {
            _gate.Exit();
        }
    }
}
