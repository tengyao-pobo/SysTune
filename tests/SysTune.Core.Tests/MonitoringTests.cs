using SysTune.Core.Monitoring;

namespace SysTune.Core.Tests;

public class MonitoringTests
{
    [Fact]
    public void CpuUsage_FirstSampleIsNull_ThenComputesFromDeltas()
    {
        var calc = new CpuUsageCalculator();
        Assert.Null(calc.Update(idle: 1000, kernel: 2000, user: 1000));
        // total delta = 400 (kernel 300 incl idle 100 + user 100), idle delta 100 → 75 %
        Assert.Equal(75, calc.Update(idle: 1100, kernel: 2300, user: 1100)!.Value, 3);
    }

    [Fact]
    public void CpuUsage_CounterWrapIsIgnored()
    {
        var calc = new CpuUsageCalculator();
        calc.Update(1000, 2000, 1000);
        Assert.Null(calc.Update(10, 20, 10));
        Assert.NotNull(calc.Update(20, 40, 20));
    }

    [Fact]
    public void Rate_ComputesPerSecond_AndRebasesOnReset()
    {
        var r = new RateCalculator();
        Assert.Equal(0, r.Update(1000, TimeSpan.FromSeconds(1)));
        Assert.Equal(500, r.Update(2000, TimeSpan.FromSeconds(3)), 3);
        Assert.Equal(0, r.Update(10, TimeSpan.FromSeconds(4)));   // counter went backwards
        Assert.Equal(90, r.Update(100, TimeSpan.FromSeconds(5)), 3);
    }

    [Fact]
    public void SampleHistory_WrapsAndKeepsNewest()
    {
        var h = new SampleHistory(3);
        foreach (var v in new double[] { 1, 2, 3, 4, double.NaN }) h.Add(v);
        Assert.Equal([3, 4, 0], h.ToArray());
        Assert.Equal(4, h.Max());
        Span<double> two = stackalloc double[2];
        Assert.Equal(2, h.CopyTo(two));
        Assert.Equal(4, two[0]);
    }

    [Fact]
    public void GpuEngine_TakesBusiestEngineTypePerAdapter()
    {
        var instances = new Dictionary<string, double>
        {
            ["pid_100_luid_0x00000000_0x0000C5E1_phys_0_eng_0_engtype_3D"] = 20,
            ["pid_200_luid_0x00000000_0x0000C5E1_phys_0_eng_0_engtype_3D"] = 15,
            ["pid_200_luid_0x00000000_0x0000C5E1_phys_0_eng_3_engtype_VideoDecode"] = 30,
            ["pid_300_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_Compute_0"] = 80,
            ["pid_300_luid_0x00000000_0x0000AAAA_phys_0_eng_1_engtype_Compute_0"] = 50,
            ["garbage"] = 99,
        };
        var result = GpuEngineAggregator.Aggregate(instances);
        Assert.Equal(35, result[0xC5E1], 3);   // 3D sum 35 > decode 30
        Assert.Equal(100, result[0xAAAA], 3);  // clamped
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GpuLuid_ParsesHighAndLowParts()
    {
        Assert.Equal(0x0000000100000002L, GpuEngineAggregator.ParseLuid("luid_0x00000001_0x00000002_phys_0"));
        Assert.Null(GpuEngineAggregator.ParseLuid("nope"));
        var sums = GpuEngineAggregator.SumByLuid([new("luid_0x00000000_0x00000010_phys_0", 100), new("luid_0x00000000_0x00000010_phys_1", 50)]);
        Assert.Equal(150, sums[0x10]);
    }

    [Fact]
    public void Snapshots_ComputeUsedPercent()
    {
        var m = new MemorySnapshot(16, 4, 0, 0);
        Assert.Equal(12UL, m.UsedBytes);
        Assert.Equal(75, m.UsedPercent);
        var d = new DriveSnapshot("C:\\", "", "NTFS", 0, 0);
        Assert.Equal(0, d.UsedPercent);
    }
}
