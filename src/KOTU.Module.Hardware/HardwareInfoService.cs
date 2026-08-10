using System.Management;
using System.Runtime.InteropServices;

namespace KOTU.Module.Hardware;

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
    // 섹션 순서는 사용자 지정: CPU → GPU → RAM → Motherboard → Storage → (Network, A20) → System
    public static IReadOnlyList<HardwareSection> Collect() =>
    [
        Safe("CPU", CollectCpu),
        Safe("GPU", CollectGpu),
        Safe("RAM", CollectMemory),
        Safe("Motherboard", CollectBoard),
        Safe("Storage", CollectStorage),
        Safe("Network", CollectNetwork),
        Safe("System", CollectSystem),
    ];

    private static HardwareSection Safe(string title, Func<List<HardwareItem>> collect)
    {
        try
        {
            var items = collect();
            return new HardwareSection(title,
                items.Count > 0 ? items : [new HardwareItem("No data", "-")]);
        }
        catch (Exception ex)
        {
            return new HardwareSection(title, [new HardwareItem("Read failed", ex.Message)]);
        }
    }

    // ---------- 섹션별 수집 ----------

    private static List<HardwareItem> CollectCpu()
    {
        var items = new List<HardwareItem>();
        foreach (var row in Query(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor"))
        {
            items.Add(new HardwareItem("Model", S(row["Name"])));
            items.Add(new HardwareItem("Cores / Threads",
                $"{U32(row["NumberOfCores"])} / {U32(row["NumberOfLogicalProcessors"])}"));
            items.Add(new HardwareItem("Max clock", HardwareFormat.MegaHertz(U32(row["MaxClockSpeed"]))));
            items.Add(new HardwareItem("L2 / L3 cache",
                $"{HardwareFormat.KiloBytes(U32(row["L2CacheSize"]))} / {HardwareFormat.KiloBytes(U32(row["L3CacheSize"]))}"));
            items.Add(new HardwareItem("Socket", S(row["SocketDesignation"])));
        }
        return items;
    }

    private static List<HardwareItem> CollectBoard()
    {
        var items = new List<HardwareItem>();
        foreach (var row in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            items.Add(new HardwareItem("Manufacturer", S(row["Manufacturer"])));
            items.Add(new HardwareItem("Model", S(row["Product"])));
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
            items.Insert(0, new HardwareItem("Total capacity", HardwareFormat.Bytes(total)));
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
                value += $" · VRAM {HardwareFormat.Bytes(vram)} (WMI caps this at 4 GB)";
            value += $" · driver {S(row["DriverVersion"])}";
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
                $"{HardwareFormat.Bytes((ulong)drive.AvailableFreeSpace)} free of {HardwareFormat.Bytes((ulong)drive.TotalSize)}"
                + (string.IsNullOrEmpty(drive.VolumeLabel) ? "" : $" · {drive.VolumeLabel}")));
        }
        return items;
    }

    // ---------- Network (A20) ----------

    /// <summary>업/다운 전송률 차분용 직전 관측치. Collect는 폴러 스레드에서만 불린다 — 잠금 불필요.</summary>
    private static (long Rx, long Tx, DateTime At)? _lastTraffic;

    /// <summary>
    /// 연결 여부·대표 어댑터·링크 속도·업/다운 전송률(A20).
    /// 전송률은 활성 어댑터 합계 바이트의 스펙 수집 간격(2초) 차분 — 첫 수집은 "measuring…".
    /// WMI가 아닌 NetworkInterface(GetIPStatistics) 사용: 가볍고 관리자 권한 불필요.
    /// </summary>
    private static List<HardwareItem> CollectNetwork()
    {
        var items = new List<HardwareItem>();
        var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType is not
                            (System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                             or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel))
            .ToList();

        var connected = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()
                        && nics.Count > 0;
        items.Add(new HardwareItem("Status", connected ? "Connected" : "Disconnected"));

        // 대표 어댑터 = 기본 게이트웨이가 있는 것(인터넷 경로) — 없으면 첫 활성 어댑터
        var primary = nics.FirstOrDefault(HasGateway) ?? nics.FirstOrDefault();
        if (primary is not null)
        {
            items.Add(new HardwareItem("Adapter",
                $"{primary.Name} · {primary.Description}"));
            items.Add(new HardwareItem("Link speed", HardwareFormat.BitsPerSecond(primary.Speed)));
        }

        // 업/다운 스트림: 활성 어댑터 합계 바이트의 시간 차분
        long rx = 0, tx = 0;
        foreach (var nic in nics)
        {
            try
            {
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch
            {
                // 어댑터 하나 실패는 무시 — 나머지 합계로 계속
            }
        }
        var now = DateTime.UtcNow;
        if (_lastTraffic is { } last && now > last.At && rx >= last.Rx && tx >= last.Tx)
        {
            var seconds = (now - last.At).TotalSeconds;
            items.Add(new HardwareItem("Down / Up",
                $"{HardwareFormat.BytesPerSecond((rx - last.Rx) / seconds)} ↓ · "
                + $"{HardwareFormat.BytesPerSecond((tx - last.Tx) / seconds)} ↑"));
        }
        else
        {
            items.Add(new HardwareItem("Down / Up", "measuring…")); // 첫 수집·어댑터 변경 직후
        }
        _lastTraffic = (rx, tx, now);
        return items;
    }

    private static bool HasGateway(System.Net.NetworkInformation.NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<HardwareItem> CollectSystem()
    {
        var items = new List<HardwareItem>();
        foreach (var row in Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
        {
            items.Add(new HardwareItem("PC", $"{S(row["Manufacturer"])} {S(row["Model"])}".Trim()));
        }
        items.Add(new HardwareItem("OS", RuntimeInformation.OSDescription));
        items.Add(new HardwareItem("Computer name", Environment.MachineName));
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
