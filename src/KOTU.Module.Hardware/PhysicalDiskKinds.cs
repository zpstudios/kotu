using System.Management;

namespace KOTU.Module.Hardware;

/// <summary>
/// 드라이브 문자 → 물리 디스크 종류(SSD·SATA (HDD)·NVMe·USB) 조회 (A22, v0.108.0).
/// DriveInfo만으로는 SSD/HDD를 구분할 수 없어 WMI(MSFT_PhysicalDisk의 MediaType/BusType)를 쓴다.
///
/// 하드웨어 모듈에 두는 이유: WMI 의존(System.Management 패키지)이 이미 이 모듈에만 있고
/// 셸(KOTU.App)이 이 모듈을 참조하고 있어, 새 패키지 참조 없이 기존 조회 방식을 그대로 재사용한다.
/// 조회 로직은 이 한 곳뿐이고, 드라이브 열거·용량 계산·표기는 Core(DriveStatus)가 맡는다.
///
/// 종류는 사실상 바뀌지 않으므로 프로세스 수명 동안 1회만 조회해 캐시한다
/// (용량·사용률은 DriveInfo로 싸게 얻으므로 호출자가 30초 주기로 따로 갱신한다).
/// 조회는 수백 ms 이상 걸릴 수 있다 — 반드시 워커 스레드에서 부를 것.
/// </summary>
public static class PhysicalDiskKinds
{
    private static readonly object Gate = new();
    private static Dictionary<char, string>? _cache;

    /// <summary>
    /// 드라이브 루트("C:\" 또는 "C:")의 종류. 매핑이 없거나 WMI가 실패하면 null —
    /// 호출자(DriveStatus)가 DriveType 근사 표기로 대체한다.
    /// </summary>
    public static string? Lookup(string root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        var letter = char.ToUpperInvariant(root[0]);
        return Map().TryGetValue(letter, out var kind) ? kind : null;
    }

    private static Dictionary<char, string> Map()
    {
        lock (Gate)
        {
            return _cache ??= Load(); // 프로세스 1회 조회 (A22 캐시 규칙)
        }
    }

    /// <summary>
    /// MSFT_Partition(드라이브 문자 → 디스크 번호)과 MSFT_PhysicalDisk(디스크 번호 → 종류)를 잇는다.
    /// 실패하면 빈 사전 — 폴백(DriveType 근사)이 대신하고 예외는 밖으로 던지지 않는다.
    /// </summary>
    private static Dictionary<char, string> Load()
    {
        var map = new Dictionary<char, string>();
        try
        {
            // 저장소 전용 네임스페이스 — 기본 root\CIMV2에는 MSFT_* 클래스가 없다.
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");

            var kinds = new Dictionary<uint, string>();
            foreach (var row in Query(scope, "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"))
            {
                if (!uint.TryParse(Text(Value(row, "DeviceId")), out var number)) continue;
                if (Describe(U16(Value(row, "MediaType")), U16(Value(row, "BusType"))) is { } kind)
                    kinds[number] = kind;
            }

            foreach (var row in Query(scope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            {
                var letter = Letter(Value(row, "DriveLetter"));
                if (letter == '\0') continue; // 드라이브 문자가 없는 파티션(복구·EFI 등)
                if (kinds.TryGetValue(U32(Value(row, "DiskNumber")), out var kind))
                    map[letter] = kind;
            }
        }
        catch
        {
            // WMI 미가용·권한·공급자 오류 — 종류 없이 진행한다.
        }
        return map;
    }

    /// <summary>
    /// MSFT_PhysicalDisk 코드 → 표기. MediaType 3 = HDD, 4 = SSD, 5 = SCM /
    /// BusType 7 = USB, 11 = SATA, 17 = NVMe. 표기 문자열은 사용자 확정
    /// (SSD · SATA (HDD) · NVMe · USB). 모르는 조합은 null → 폴백.
    /// </summary>
    private static string? Describe(ushort mediaType, ushort busType) => (mediaType, busType) switch
    {
        (_, 17) => "NVMe",
        (_, 7) => "USB",
        (4, _) => "SSD",
        (3, 11) => "SATA (HDD)",
        (3, _) => "HDD",
        (5, _) => "SCM",
        _ => null,
    };

    // ---------- WMI 헬퍼 (HardwareInfoService.Query와 같은 형태, 네임스페이스만 다르다) ----------

    /// <summary>WQL 실행 → 행마다 (속성명→값) 사전. 개별 속성 접근 실패는 null 처리.</summary>
    private static List<Dictionary<string, object?>> Query(ManagementScope scope, string wql)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var obj in results)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in obj.Properties)
            {
                try { row[property.Name] = property.Value; }
                catch { row[property.Name] = null; }
            }
            rows.Add(row);
            obj.Dispose();
        }
        return rows;
    }

    private static object? Value(Dictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) ? value : null;

    private static string Text(object? value) => value?.ToString()?.Trim() ?? string.Empty;

    /// <summary>MSFT_Partition.DriveLetter는 char16 — 문자가 없는 파티션은 0이 온다.</summary>
    private static char Letter(object? value)
    {
        var letter = value switch
        {
            char c => c,
            ushort u => (char)u,
            string s when s.Length > 0 => s[0],
            _ => '\0',
        };
        return char.IsLetter(letter) ? char.ToUpperInvariant(letter) : '\0';
    }

    private static ushort U16(object? value)
    {
        try { return value is null ? (ushort)0 : Convert.ToUInt16(value); }
        catch { return 0; }
    }

    private static uint U32(object? value)
    {
        try { return value is null ? 0u : Convert.ToUInt32(value); }
        catch { return 0u; }
    }
}
