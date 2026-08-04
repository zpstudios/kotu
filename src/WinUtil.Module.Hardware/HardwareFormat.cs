namespace WinUtil.Module.Hardware;

/// <summary>하드웨어 수치 표기. UI 비의존 — 단위 테스트 대상.</summary>
public static class HardwareFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>바이트 → 이진(1024) 단위 표기. 정수로 떨어지면 소수점 없이, 아니면 한 자리.</summary>
    public static string Bytes(ulong bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 100 || value == Math.Floor(value)
            ? $"{value:0} {Units[unit]}"
            : $"{value:0.#} {Units[unit]}";
    }

    /// <summary>KB 단위 값(WMI 캐시 크기 등) → 표기.</summary>
    public static string KiloBytes(uint kilobytes) => Bytes((ulong)kilobytes * 1024);

    /// <summary>MHz → 1 GHz 이상이면 GHz 표기.</summary>
    public static string MegaHertz(uint megahertz) =>
        megahertz >= 1000 ? $"{megahertz / 1000.0:0.##} GHz" : $"{megahertz} MHz";

    /// <summary>SMBIOS 메모리 타입 코드 → DDR 세대 이름. 모르면 빈 문자열.</summary>
    public static string MemoryType(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };
}
