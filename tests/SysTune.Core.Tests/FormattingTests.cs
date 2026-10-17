using SysTune.Core.Formatting;

namespace SysTune.Core.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(17179869184.0, "16.0 GB")]
    [InlineData(-1, "—")]
    public void Bytes(double input, string expected) => Assert.Equal(expected, Fmt.Bytes(input));

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(999, "999 B/s")]
    [InlineData(1024 * 5.5, "5.50 KB/s")]
    [InlineData(1024 * 1024 * 12.34, "12.3 MB/s")]
    [InlineData(1024 * 1024 * 250, "250 MB/s")]
    [InlineData(double.NaN, "—")]
    public void Rate(double input, string expected) => Assert.Equal(expected, Fmt.Rate(input));

    [Fact]
    public void Percent_ClampsAndHandlesNull()
    {
        Assert.Equal("—", Fmt.Percent(null));
        Assert.Equal("100%", Fmt.Percent(130));
        Assert.Equal("43%", Fmt.Percent(42.6));
    }

    [Fact]
    public void Temperature_ConvertsToFahrenheit()
    {
        Assert.Equal("50°C", Fmt.Temperature(50));
        Assert.Equal("122°F", Fmt.Temperature(50, fahrenheit: true));
        Assert.Equal("—", Fmt.Temperature(null));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(61_234, "1:01")]
    [InlineData(3_723_000, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void MediaTime(int ms, string expected) => Assert.Equal(expected, Fmt.MediaTime(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void Uptime_UsesLargestUnits()
    {
        Assert.Equal("2 天 3 小時", Fmt.Uptime(new TimeSpan(2, 3, 4, 5)));
        Assert.Equal("5 小時 6 分", Fmt.Uptime(new TimeSpan(5, 6, 0)));
        Assert.Equal("7 分鐘", Fmt.Uptime(TimeSpan.FromMinutes(7.5)));
    }
}
