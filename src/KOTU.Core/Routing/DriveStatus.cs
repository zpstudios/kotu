namespace KOTU.Core.Routing;

/// <summary>
/// 하단 바 드라이브 줄의 항목 하나 (A22, v0.108.0).
/// 예: Name "C:", Kind "SSD", Capacity "412 GB of 931 GB (44%)", Ratio 0.44.
/// </summary>
/// <param name="Name">드라이브명("C:").</param>
/// <param name="Kind">종류 표기(SSD·SATA (HDD)·NVMe·USB 등). 모르면 null — 표시에서 칸 자체를 생략한다.</param>
/// <param name="Capacity">"사용량 of 전체 (사용률%)" 한 줄.</param>
/// <param name="Ratio">사용률 0..1 — 막대 그래프 채움 비율.</param>
public sealed record DriveUsage(string Name, string? Kind, string Capacity, double Ratio);

/// <summary>
/// 시스템 드라이브 목록·사용량 수집 (A22, v0.108.0).
/// v0.47.0의 "현재 파일이 있는 드라이브 한 줄"(Describe)을 대체한다 — 이제 대상은 시스템의
/// 모든 드라이브이고, 표시 시점도 반대(파일이 열려 있지 않을 때만)로 뒤집혔다.
/// 종류(SSD/NVMe/USB)는 DriveInfo로 알 수 없어 호출자가 조회 함수(kindLookup)를 넘긴다 —
/// Core는 Windows·WMI 비의존이어야 하므로 실제 WMI 조회는 하드웨어 모듈
/// (KOTU.Module.Hardware.PhysicalDiskKinds)에 있다.
/// 조회는 느릴 수 있으니(WMI 수백 ms~수 초) 반드시 워커 스레드에서 부를 것.
/// </summary>
public static class DriveStatus
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const long Terabyte = Gigabyte * 1024;

    /// <summary>
    /// 준비된 드라이브 전부(드라이브 문자 순). 준비되지 않은 드라이브(IsReady == false —
    /// 연결 끊긴 네트워크 드라이브·빈 CD롬 등)는 제외하고, 드라이브 하나가 실패해도
    /// (권한·매체 제거) 그 드라이브만 건너뛴다.
    /// </summary>
    /// <param name="kindLookup">
    /// 드라이브 루트("C:\")를 받아 종류 문자열을 돌려주는 조회. null이거나 못 찾으면
    /// DriveType 기준 근사 표기로 대체한다.
    /// </param>
    public static IReadOnlyList<DriveUsage> Collect(Func<string, string?>? kindLookup = null)
    {
        var list = new List<DriveUsage>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return list; // 열거 자체가 실패하면 표시를 생략한다(부가 정보)
        }

        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady) continue;
                var total = drive.TotalSize;
                if (total <= 0) continue;

                var used = Math.Max(0, total - drive.TotalFreeSpace);
                var ratio = Math.Clamp((double)used / total, 0, 1);
                var percent = (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
                list.Add(new DriveUsage(
                    drive.Name.TrimEnd('\\', '/'),
                    Kind(drive, kindLookup),
                    $"{FormatCapacity(used)} of {FormatCapacity(total)} ({percent}%)",
                    ratio));
            }
            catch
            {
                // 드라이브 하나의 실패로 나머지를 잃지 않는다.
            }
        }
        return list;
    }

    /// <summary>
    /// 종류 표기: WMI 조회(kindLookup) 우선, 실패하거나 매핑이 없으면 DriveType 근사 표기,
    /// 그마저 모르면 null — 호출자가 종류 칸을 통째로 생략한다(빈 괄호를 남기지 않는다).
    /// </summary>
    private static string? Kind(DriveInfo drive, Func<string, string?>? kindLookup)
    {
        try
        {
            if (kindLookup?.Invoke(drive.Name) is { Length: > 0 } kind) return kind;
        }
        catch
        {
            // WMI 실패는 폴백으로 흡수 — 예외가 목록 전체를 막으면 안 된다.
        }

        return drive.DriveType switch
        {
            DriveType.Fixed => "Local",
            DriveType.Removable => "Removable",
            DriveType.Network => "Network",
            DriveType.CDRom => "CD-ROM",
            _ => null,
        };
    }

    /// <summary>
    /// 용량 표기(1024 기준, 사용자 확정): 1 TB 미만은 GB 정수, 1 TB 이상은 TB 소수 1자리.
    /// </summary>
    public static string FormatCapacity(long bytes)
    {
        if (bytes < 0) bytes = 0;
        return bytes >= Terabyte
            ? $"{(double)bytes / Terabyte:0.0} TB"
            : $"{Math.Round((double)bytes / Gigabyte, MidpointRounding.AwayFromZero):0} GB";
    }
}
