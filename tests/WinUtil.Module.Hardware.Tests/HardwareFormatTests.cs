using Xunit;

namespace WinUtil.Module.Hardware.Tests;

public class HardwareFormatTests
{
    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(512UL, "512 B")]
    [InlineData(1024UL, "1 KB")]
    [InlineData(1536UL * 1024 * 1024, "1.5 GB")]          // 1.5 GiB
    [InlineData(16UL * 1024 * 1024 * 1024, "16 GB")]      // 16 GiB
    [InlineData(512UL * 1024 * 1024, "512 MB")]
    public void Bytes_이진_단위로_표기한다(ulong bytes, string expected) =>
        Assert.Equal(expected, HardwareFormat.Bytes(bytes));

    [Fact]
    public void Bytes_100_이상은_소수점을_버린다()
    {
        // 1TB 디스크의 실제 바이트 수 → 931.5GiB 근방, 100 이상이므로 정수 표기
        Assert.Equal("932 GB", HardwareFormat.Bytes(1_000_204_886_016));
    }

    [Theory]
    [InlineData(800u, "800 MHz")]
    [InlineData(1000u, "1 GHz")]
    [InlineData(3600u, "3.6 GHz")]
    [InlineData(5432u, "5.43 GHz")]
    public void MegaHertz_1GHz_이상은_GHz로(uint mhz, string expected) =>
        Assert.Equal(expected, HardwareFormat.MegaHertz(mhz));

    [Fact]
    public void KiloBytes_캐시_크기_표기()
    {
        Assert.Equal("32 MB", HardwareFormat.KiloBytes(32_768));
        Assert.Equal("512 KB", HardwareFormat.KiloBytes(512));
    }

    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(99, "")]
    public void MemoryType_SMBIOS_코드를_DDR_세대로(int code, string expected) =>
        Assert.Equal(expected, HardwareFormat.MemoryType(code));

    [Theory]
    [InlineData(-1L, "-")]                    // NIC Speed 미보고
    [InlineData(0L, "-")]
    [InlineData(100_000_000L, "100 Mbps")]    // 100M 이더넷
    [InlineData(1_000_000_000L, "1 Gbps")]    // 기가비트
    [InlineData(866_700_000L, "866.7 Mbps")]  // Wi-Fi 5 링크
    [InlineData(2_500_000_000L, "2.5 Gbps")]  // 2.5G 이더넷
    public void BitsPerSecond_링크_속도_표기(long bps, string expected) =>
        Assert.Equal(expected, HardwareFormat.BitsPerSecond(bps)); // A20

    [Theory]
    [InlineData(0.0, "0 B/s")]
    [InlineData(1024.0, "1 KB/s")]
    [InlineData(12.3 * 1024 * 1024, "12.3 MB/s")]
    [InlineData(-1.0, "-")]
    public void BytesPerSecond_전송률_표기(double rate, string expected) =>
        Assert.Equal(expected, HardwareFormat.BytesPerSecond(rate)); // A20
}
