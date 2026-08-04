using System.Management;
using System.Runtime.InteropServices;

namespace WinUtil.Module.Hardware;

/// <summary>이름-값 한 줄.</summary>
public sealed record HardwareItem(string Label, string Value);

/// <summary>섹션(CPU/메모리 등) 하나.</summary>
public sealed record HardwareSection(string Title, IReadOnlyList<HardwareItem> Items);

/// <summary>
/// WMI 기반 스펙 수집(CPU-Z류 '정보' 영역). 관리자 권한 불필요.
/// 온도·팬 등 센서는 커널 드라이버가 필요하므로 Phase 5b(LibreHardwareMonitor)에서.
/// 호출은 느릴 수 있으니(WMI 특성) 반드시 백그라운드 스레드에서.
/// </summary>
public static class HardwareInfoService
{
    public static IReadOnlyList<HardwareSection> Collect() =>
    [
        Safe("CPU", CollectCpu),
        Safe("메인보드", CollectBoard),
        Safe("메모리", CollectMemory),
        Safe("그래픽", CollectGpu),
        Safe("저장장치", CollectStorage),
        Safe("시스템", CollectSystem),
    ];

    private static HardwareSection Safe(string title, Func<List<HardwareItem>> collect)
    {
        try
        {
            var items = collect();
            return new HardwareSection(title,
                items.Count > 0 ? items : [new HardwareItem("정보 없음", "-")]);
        }
        catch (Exception ex)
        {
            return new HardwareSection(title, [new HardwareItem("읽기 실패", ex.Message)]);
        }
    }

    // ---------- 섹션별 수집 ----------

    private static List<HardwareItem> CollectCpu()
    {
        var items = new List<HardwareItem>();
        foreach (var row in Query(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor"))
        {
            items.Add(new HardwareItem("모델", S(row["Name"])));
            items.Add(new HardwareItem("코어 / 스레드",
                $"{U32(row["NumberOfCores"])} / {U32(row["NumberOfLogicalProcessors"])}"));
            items.Add(new HardwareItem("최대 클럭", HardwareFormat.MegaHertz(U32(row["MaxClockSpeed"]))));
            items.Add(new HardwareItem("L2 / L3 캐시",
                $"{HardwareFormat.KiloBytes(U32(row["L2CacheSize"]))} / {HardwareFormat.KiloBytes(U32(row["L3CacheSize"]))}"));
            items.Add(new HardwareItem("소켓", S(row["SocketDesignation"])));
        }
        return items;
    }

    private static List<HardwareItem> CollectBoard()
    {
        var items = new List<HardwareItem>();
        foreach (var row in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            items.Add(new HardwareItem("제조사", S(row["Manufacturer"])));
            items.Add(new HardwareItem("모델", S(row["Product"])));
        }
        foreach (var row in Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            var date = S(row["ReleaseDate"]);
            var formatted = date.Length >= 8 ? $"{date[..4]}-{date[4..6]}-{date[6..8]}" : date;
            items.Add(new HardwareItem("BIOS", $"{S(row["SMBIOSBIOSVersion"])} ({formatted})"));
        }
        return items;
    }

    private static List<HardwareItem> CollectMemory()
    {
        var items = new List<HardwareItem>();
        ulong total = 0;
        foreach (var row in Query(
            "SELECT DeviceLocator, Capacity, Speed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            var capacity = U64(row["Capacity"]);
            total += capacity;

            var type = HardwareFormat.MemoryType((int)U32(row["SMBIOSMemoryType"]));
            var speed = U32(row["Speed"]);
            var detail = string.Join(" ", new[]
            {
                HardwareFormat.Bytes(capacity),
                type.Length > 0 && speed > 0 ? $"{type}-{speed}" : type,
                S(row["Manufacturer"]),
                S(row["PartNumber"]),
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            items.Add(new HardwareItem(S(row["DeviceLocator"]), detail));
        }
        if (total > 0)
            items.Insert(0, new HardwareItem("총 용량", HardwareFormat.Bytes(total)));
        return items;
    }

    private static List<HardwareItem> CollectGpu()
    {
        var items = new List<HardwareItem>();
        var index = 1;
        foreach (var row in Query(
            "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController"))
        {
            var vram = U64(row["AdapterRAM"]);
            var value = S(row["Name"]);
            if (vram > 0)
                value += $" · VRAM {HardwareFormat.Bytes(vram)}(표기 한계상 최대 4 GB)";
            value += $" · 드라이버 {S(row["DriverVersion"])}";
            items.Add(new HardwareItem($"GPU {index++}", value));
        }
        return items;
    }

    private static List<HardwareItem> CollectStorage()
    {
        var items = new List<HardwareItem>();
        foreach (var row in Query("SELECT Model, Size, InterfaceType FROM Win32_DiskDrive"))
        {
            var size = U64(row["Size"]);
            var iface = S(row["InterfaceType"]);
            var value = HardwareFormat.Bytes(size)
                + (iface.Length > 0 ? $" · {iface}" : "");
            items.Add(new HardwareItem(S(row["Model"]), value));
        }

        foreach (var drive in DriveInfo.GetDrives()
                     .Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            items.Add(new HardwareItem(
                drive.Name.TrimEnd('\\'),
                $"{HardwareFormat.Bytes((ulong)drive.TotalSize)} 중 {HardwareFormat.Bytes((ulong)drive.AvailableFreeSpace)} 남음"
                + (string.IsNullOrEmpty(drive.VolumeLabel) ? "" : $" · {drive.VolumeLabel}")));
        }
        return items;
    }

    private static List<HardwareItem> CollectSystem()
    {
        var items = new List<HardwareItem>();
        foreach (var row in Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
        {
            items.Add(new HardwareItem("PC", $"{S(row["Manufacturer"])} {S(row["Model"])}".Trim()));
        }
        items.Add(new HardwareItem("OS", RuntimeInformation.OSDescription));
        items.Add(new HardwareItem("컴퓨터 이름", Environment.MachineName));
        return items;
    }

    // ---------- WMI 헬퍼 ----------

    /// <summary>WQL 실행 → 행마다 (속성명→값) 사전. 개별 속성 접근 실패는 null 처리.</summary>
    private static List<Dictionary<string, object?>> Query(string wql)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var searcher = new ManagementObjectSearcher(wql);
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

    private static string S(object? value) => value?.ToString()?.Trim() ?? "";

    private static uint U32(object? value)
    {
        try { return value is null ? 0 : Convert.ToUInt32(value); }
        catch { return 0; }
    }

    private static ulong U64(object? value)
    {
        try { return value is null ? 0 : Convert.ToUInt64(value); }
        catch { return 0; }
    }
}
