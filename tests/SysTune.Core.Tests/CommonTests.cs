using SysTune.Core.Common;
using SysTune.Core.Logging;

namespace SysTune.Core.Tests;

public class CommonTests
{
    [Fact]
    public void Backoff_GrowsExponentiallyWithinJitterAndCaps()
    {
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), new Random(42));
        var d1 = b.Next();
        var d2 = b.Next();
        var d3 = b.Next();
        Assert.InRange(d1.TotalSeconds, 0.8, 1.2);
        Assert.InRange(d2.TotalSeconds, 1.6, 2.4);
        Assert.InRange(d3.TotalSeconds, 3.2, 4.8);
        for (var i = 0; i < 30; i++) Assert.True(b.Next() <= TimeSpan.FromSeconds(10));
        b.Reset();
        Assert.InRange(b.Next().TotalSeconds, 0.8, 1.2);
    }

    [Fact]
    public async Task AsyncSignal_WakesWaiterAndTimesOut()
    {
        var s = new AsyncSignal();
        Assert.False(await s.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
        var wait = s.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        s.Set();
        Assert.True(await wait);
        // auto-reset
        Assert.False(await s.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public void FileLog_RollsFiles()
    {
        var dir = Directory.CreateTempSubdirectory("systune-log");
        try
        {
            var path = Path.Combine(dir.FullName, "app.log");
            var log = new FileLog(path, LogLevel.Debug, maxBytes: 200, maxFiles: 3);
            for (var i = 0; i < 20; i++) log.Info(new string('x', 50) + i);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            Assert.True(File.Exists(path + ".2"));
            Assert.False(File.Exists(path + ".3"));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void FileLog_RespectsMinimumLevel()
    {
        var dir = Directory.CreateTempSubdirectory("systune-log");
        try
        {
            var path = Path.Combine(dir.FullName, "app.log");
            var log = new FileLog(path, LogLevel.Warn);
            log.Info("hidden");
            log.Error("shown", new InvalidOperationException("boom"));
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("hidden", text);
            Assert.Contains("[ERROR] shown", text);
            Assert.Contains("boom", text);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
