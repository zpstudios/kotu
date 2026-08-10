using Xunit;

namespace KOTU.Module.Audio.Tests;

public class TimeTextTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59_999, "0:59")]          // 밀리초는 버림
    [InlineData(65_000, "1:05")]
    [InlineData(3_599_999, "59:59")]      // 1시간 직전까지는 m:ss
    [InlineData(3_600_000, "1:00:00")]
    [InlineData(3_661_000, "1:01:01")]
    [InlineData(36_000_000, "10:00:00")]
    public void Format_밀리초를_시간표기로(long ms, string expected) =>
        Assert.Equal(expected, TimeText.Format(ms));

    [Fact]
    public void Format_음수는_0으로() => Assert.Equal("0:00", TimeText.Format(-1234));
}
