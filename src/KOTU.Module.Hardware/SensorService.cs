using System.Management;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace KOTU.Module.Hardware;

/// <summary>
/// 센서 10채널 한 프레임(A17). 값을 못 구한 채널(하드웨어 미지원·권한 부족)은 null.
/// 단위: 온도 °C, 전력 W, 부하 %, 클럭 MHz, 팬 RPM.
/// </summary>
public sealed record SensorFrame(
    DateTime Timestamp,
    float? CpuTemp, float? CpuPower, float? CpuLoad, float? CpuClock,
    float? GpuTemp, float? GpuPower, float? GpuLoad,
    float? RamLoad, float? FanRpm, float? SsdTemp)
{
    /// <summary>수집 전·실패 프레임 — Timestamp가 MinValue면 아직 유효 데이터가 아니다.</summary>
    public static readonly SensorFrame Empty = new(DateTime.MinValue,
        null, null, null, null, null, null, null, null, null, null);
}

/// <summary>
/// LibreHardwareMonitor 기반 센서 수집(A17, Phase 5b).
///
/// - 모든 LHM 접근은 폴러 스레드(HardwareModule.Poller) 한 곳에서만 일어난다.
///   _gate 잠금은 앱 종료 시 Shutdown()과의 경합만 막기 위한 것.
/// - Open()은 첫 Read에서 지연 수행(드라이버 디바이스 열기 + 장치 열거로 1~3초 걸릴 수 있음 —
///   폴러가 BelowNormal 백그라운드라 UI는 안 막힌다. 뷰는 그동안 Busy 링 표시).
/// - LHM 0.9.5+는 WinRing0가 제거되고 별도 설치형 서명 드라이버 PawnIO 기반이다(A47 조사 —
///   docs/A47-sensor-access-research.md §1.3). 라이브러리는 드라이버를 동봉하지 않으며, 이미
///   설치된 \\.\PawnIO 디바이스를 열어 내장 서명 모듈만 로드한다. 관리자 권한과 PawnIO 설치가
///   모두 있어야 CPU 온도·전력·클럭(MSR)·팬(SuperIO)이 나오고, SSD 온도(SMART)는 승격만으로
///   회복될 수 있다. 어느 쪽이 빠져도 예외 없이 해당 채널만 조용히 null이 된다(침묵 저하).
///   GPU(벤더 API)·RAM·CPU 부하는 비관리자에서도 나온다. CPU 클럭만은 LHM이 못 줄 때
///   WMI 성능 카운터 근사값으로 폴백한다(A47 ② — ClockApprox).
/// - Storage 갱신은 10초마다 1회(A29에서 폴링 횟수 기반 → 시간 기반으로 교체 — 주기가
///   50~5000ms 어디로 바뀌어도 SMART 부하가 일정) — SMART 질의는 상대적으로 무겁고
///   드라이브 온도는 느리게 변한다. 센서 값은 다음 갱신까지 마지막 값을 유지한다.
/// - 최근 프레임은 링 버퍼 6000개(A146/v0.165.0에서 600 → 6000)에 쌓아 그래프 이력으로 쓴다 —
///   최단 주기 50ms에서도 300초를 담으므로 하단 바 긴 그래프는 **주기와 무관하게 항상 5분치**다.
///   구독자가 없어 폴러가 휴면하는 동안은 이력에 공백이 생긴다(의도된 동작 — 수집 비용 0 유지).
/// </summary>
public static class SensorService
{
    /// <summary>
    /// 그래프 이력 링 용량(A146/v0.165.0: 600 → 6000 = `300초 / 최단 주기 50ms`).
    /// 담기는 시간 = 용량 × 리프레시 주기라 주기에 따라 달라지지만, 6000이면 A73의 전 주기
    /// (50~5000ms)에서 300초 이상을 담는다 — 뷰의 그래프 창(WindowFor = min(사양 창, 용량 × 주기))이
    /// 늘 사양 창 쪽에서 잘리므로 좌 대형 10초·센터 타일 30초·하단 바 5분이 주기와 무관하게 성립한다.
    /// 뷰가 그래프 창을 이 값으로 제한하고 기간 표기에도 그 계산값을 그대로 쓰므로(A74·A146)
    /// internal로 공개한다. 고정 용량이다 — 주기별 동적 재할당은 배열이 static readonly라 금지
    /// (A146 확정: 메모리 약 620KB는 수용된 사양).
    /// </summary>
    internal const int HistoryCapacity = 6000;

    /// <summary>Storage(SMART) 갱신 간격 — 시간 기반(A29: 폴링 주기와 무관하게 일정 부하).</summary>
    private static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(10);

    private static DateTime _lastStorageAt = DateTime.MinValue;

    /// <summary>LHM(Computer) 접근 직렬화 — 폴러 Read와 종료 Shutdown의 경합만 막는다.</summary>
    private static readonly object _gate = new();

    /// <summary>
    /// 이력 링 전용 잠금. History()는 UI 스레드에서 매 프레임 불리므로, 수집(_gate,
    /// 첫 Open 1~3초·SMART 폴링 수백 ms 가능)과 잠금을 공유하면 UI가 멈춘다 — 분리 필수.
    /// </summary>
    private static readonly object _historyGate = new();

    private static readonly SensorFrame[] _history = new SensorFrame[HistoryCapacity];
    private static Computer? _computer;
    private static bool _openFailed;   // Open 실패(드라이버 로드 불가 등) — 재시도하지 않는다
    private static bool _shutdown;     // 종료 후 재오픈 금지
    private static int _historyNext;   // 다음 쓰기 위치
    private static int _historyCount;  // 채워진 개수(≤ 용량)

    /// <summary>현재 프로세스가 관리자로 떠 있는가 — 승격 안내 UI 판단용.</summary>
    public static bool IsElevated { get; } = ComputeElevated();

    private static bool ComputeElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// PawnIO 드라이버 설치 여부(A47) — 저하 안내 UI 판단용. LHM 0.9.6은 PawnIO가 없으면
    /// 승격 상태여도 예외 없이 MSR/SuperIO 채널만 조용히 비우므로, 안내 분기가 이 판정에 기댄다.
    /// 판정 = 설치본(PawnIO.Setup)이 남기는 HKLM 언인스톨 키(A47 조사 §1.3 — LHM 자신의 감지
    /// 방식과 동일, 64/32비트 위치 모두) 또는 Program Files의 PawnIO 폴더. 디바이스 열기 시도보다
    /// 안전하다(비관리자는 어차피 못 열어 판정 축이 승격과 섞이고, 핸들 부작용도 없다).
    /// 시작 시 1회 고정 — Restart as admin 복귀는 새 프로세스라 자연히 재판정된다(폴링 불필요).
    /// </summary>
    public static bool IsPawnIoInstalled { get; } = ComputePawnIoInstalled();

    private static bool ComputePawnIoInstalled()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                if (key is not null) return true;
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                if (key is not null) return true;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return programFiles.Length > 0 && Directory.Exists(Path.Combine(programFiles, "PawnIO"));
        }
        catch
        {
            // 판정 실패 = 설치로 간주 — "설치하라"는 거짓 안내(오탐)보다 현행(침묵)이 낫다.
            return true;
        }
    }

    /// <summary>
    /// 센서 한 프레임 수집. 폴러 스레드에서 매 주기 호출된다.
    /// 실패해도 던지지 않는다 — 스냅샷의 스펙 섹션까지 잃으면 안 되기 때문. 실패 시 Empty.
    /// </summary>
    public static SensorFrame Read()
    {
        lock (_gate)
        {
            try
            {
                if (!EnsureOpen()) return SensorFrame.Empty;

                var now = DateTime.UtcNow;
                var includeStorage = now - _lastStorageAt >= StorageInterval;
                if (includeStorage) _lastStorageAt = now;
                _computer!.Accept(new UpdateVisitor(includeStorage));

                var frame = Extract(_computer);
                // A47 ②: LHM이 CPU 클럭을 못 주면(비관리자/PawnIO 미설치) WMI 근사값으로 채운다.
                // LHM 값이 있으면 근사 경로는 아예 타지 않는다(LHM 우선 — 승격+PawnIO의 현행 유지).
                if (frame.CpuClock is null && ClockApprox() is { } approxClock)
                    frame = frame with { CpuClock = approxClock };
                lock (_historyGate)
                {
                    _history[_historyNext] = frame;
                    _historyNext = (_historyNext + 1) % HistoryCapacity;
                    if (_historyCount < HistoryCapacity) _historyCount++;
                }
                return frame;
            }
            catch
            {
                return SensorFrame.Empty; // 일시 실패 — 다음 주기에 재시도(Open 자체 실패와 구분)
            }
        }
    }

    /// <summary>이력 사본(오래된 것 → 최신 순). UI 스레드에서 그래프 그릴 때 쓴다 — 수집과 잠금 분리.</summary>
    public static SensorFrame[] History()
    {
        lock (_historyGate)
        {
            var result = new SensorFrame[_historyCount];
            var start = (_historyNext - _historyCount + HistoryCapacity) % HistoryCapacity;
            for (var i = 0; i < _historyCount; i++)
                result[i] = _history[(start + i) % HistoryCapacity];
            return result;
        }
    }

    /// <summary>
    /// 앱 종료 시 호출(마지막 창 닫힘) — 드라이버 핸들·서비스를 정리한다.
    /// 종료 시점엔 뷰 구독이 다 빠져 폴러가 휴면 중이므로 Read와 거의 경합하지 않지만,
    /// 겹치더라도 _gate가 순서를 보장한다.
    /// </summary>
    public static void Shutdown()
    {
        lock (_gate)
        {
            _shutdown = true;
            try { _computer?.Close(); }
            catch { /* 종료 경로 — 실패해도 프로세스는 내려간다 */ }
            _computer = null;
        }
    }

    private static bool EnsureOpen()
    {
        if (_computer is not null) return true;
        if (_openFailed || _shutdown) return false;
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true, // SuperIO(팬) — 관리자 + PawnIO 필요(A47)
                IsStorageEnabled = true,     // SMART 온도 — 관리자 필요(PawnIO 무관 — 디스크 핸들 직접 질의)
                // Controller(외장 팬 컨트롤러)·Network(A20에서 별도)·Psu·Battery는 끈다
            };
            computer.Open();
            _computer = computer;
            return true;
        }
        catch
        {
            _openFailed = true; // 드라이버 로드 불가 등 — 매 주기 재시도해 봐야 같으니 멈춘다
            return false;
        }
    }

    // ---------- CPU 클럭 근사 폴백 (A47 ②) ----------

    /// <summary>근사 산출 실패(WMI·카운터 손상 머신 등) — Open 실패와 같은 정책으로 재시도하지 않는다.</summary>
    private static bool _clockApproxFailed;

    /// <summary>정격 클럭 MHz(Win32_Processor.MaxClockSpeed — 관리자 불필요). 첫 필요 시 1회 조회.</summary>
    private static float _clockBaseMhz;
    private static bool _clockBaseLoaded;

    /// <summary>근사 조회 간격 — 50ms 폴링에서도 WMI 부하를 초당 1회로 억제(Storage 10초와 같은 취지).</summary>
    private static readonly TimeSpan ClockApproxInterval = TimeSpan.FromSeconds(1);

    private static DateTime _lastClockApproxAt = DateTime.MinValue;
    private static float? _lastClockApprox;

    /// <summary>
    /// LHM이 CPU 클럭을 못 줄 때(MSR 접근 불가 = 비관리자 또는 PawnIO 미설치)의 근사값(A47 ②).
    /// 근사 = 정격 클럭 × "% Processor Performance"(성능 카운터의 WMI 사영
    /// Win32_PerfFormattedData_Counters_ProcessorInformation — 관리자 불필요. 터보 시 100%를
    /// 넘는 값이 나와 부스트 클럭도 대략 따라간다). 폴러 스레드(Read 안, _gate 보유)에서만
    /// 불린다 — 첫 조회가 수백 ms 걸려도 UI는 안 막힌다. 조회는 1초 1회로 제한하고 그 사이는
    /// 직전 값을 유지한다. 형식(formatted) 카운터는 두 표본의 차분이라 첫 질의가 0으로 나올 수
    /// 있어 0 이하는 미준비로 취급해 null을 돌려준다(다음 주기에 채워진다). 실패는 한 번이면
    /// 영구 포기(_openFailed와 같은 정책) — 채널은 현행대로 빈 값("—")이 된다.
    /// 근사값이라는 표기는 UI에 하지 않는다(A47 확정 — 값 채움 우선).
    /// </summary>
    private static float? ClockApprox()
    {
        if (_clockApproxFailed) return null;
        var now = DateTime.UtcNow;
        if (now - _lastClockApproxAt < ClockApproxInterval) return _lastClockApprox;
        _lastClockApproxAt = now;
        try
        {
            if (!_clockBaseLoaded)
            {
                using var cpuSearcher = new ManagementObjectSearcher(
                    "SELECT MaxClockSpeed FROM Win32_Processor");
                using var cpus = cpuSearcher.Get();
                foreach (var cpu in cpus)
                {
                    var mhz = Convert.ToSingle(cpu["MaxClockSpeed"]);
                    if (mhz > _clockBaseMhz) _clockBaseMhz = mhz; // 멀티 소켓은 최대값
                    cpu.Dispose();
                }
                _clockBaseLoaded = true;
            }
            if (_clockBaseMhz <= 0)
            {
                _clockApproxFailed = true; // 정격을 모르면 비율을 곱할 밑이 없다
                return null;
            }

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation");
            using var results = searcher.Get();
            var percent = -1.0;
            foreach (var row in results)
            {
                // 인스턴스는 "0,0"…"0,_Total"·"_Total" 꼴 — 전체 합계(_Total 계열)만 본다.
                var name = row["Name"]?.ToString() ?? "";
                if (name.EndsWith("_Total", StringComparison.OrdinalIgnoreCase))
                {
                    var value = Convert.ToDouble(row["PercentProcessorPerformance"]);
                    if (value > percent) percent = value;
                }
                row.Dispose();
            }
            _lastClockApprox = percent > 0 ? (float)(_clockBaseMhz * percent / 100.0) : null;
            return _lastClockApprox;
        }
        catch
        {
            _clockApproxFailed = true; // WMI 불가 머신 — 매 초 재시도해 봐야 같으니 멈춘다
            return null;
        }
    }

    // ---------- 채널 추출 ----------

    private static SensorFrame Extract(Computer computer)
    {
        IHardware? cpu = null, gpu = null, memory = null;
        var gpuRank = int.MaxValue;
        var fans = new List<ISensor>();
        float? ssdTemp = null;

        foreach (var hardware in computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu ??= hardware;
                    break;

                // dGPU 우선: NVIDIA → AMD → Intel(대개 iGPU) 순으로 첫 장치를 고른다
                case HardwareType.GpuNvidia when gpuRank > 0: gpu = hardware; gpuRank = 0; break;
                case HardwareType.GpuAmd when gpuRank > 1: gpu = hardware; gpuRank = 1; break;
                case HardwareType.GpuIntel when gpuRank > 2: gpu = hardware; gpuRank = 2; break;

                case HardwareType.Memory:
                    memory ??= hardware;
                    break;

                case HardwareType.Motherboard:
                    foreach (var sub in hardware.SubHardware) // SuperIO
                        CollectFans(sub, fans);
                    break;

                // 노트북 EC·AIO 쿨러의 팬도 후보에 넣는다
                case HardwareType.EmbeddedController:
                case HardwareType.Cooler:
                    CollectFans(hardware, fans);
                    break;

                case HardwareType.Storage:
                    var t = MaxValue(hardware, SensorType.Temperature);
                    if (t is not null && (ssdTemp is null || t > ssdTemp)) ssdTemp = t;
                    break;
            }
        }

        return new SensorFrame(
            DateTime.UtcNow,
            CpuTemp: cpu is null ? null
                : ByName(cpu, SensorType.Temperature, "CPU Package", "Package", "Tctl", "Tdie")
                  ?? MaxValue(cpu, SensorType.Temperature, s => !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)),
            CpuPower: cpu is null ? null
                : ByName(cpu, SensorType.Power, "CPU Package", "Package")
                  ?? MaxValue(cpu, SensorType.Power),
            CpuLoad: cpu is null ? null
                : ByName(cpu, SensorType.Load, "CPU Total")
                  ?? MaxValue(cpu, SensorType.Load, s => !s.Name.Contains("Max", StringComparison.OrdinalIgnoreCase)),
            CpuClock: cpu is null ? null
                : MaxValue(cpu, SensorType.Clock, s => !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase)),
            GpuTemp: gpu is null ? null
                : ByName(gpu, SensorType.Temperature, "GPU Core")
                  ?? FirstValue(gpu, SensorType.Temperature),
            GpuPower: gpu is null ? null
                : ByName(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core")
                  ?? FirstValue(gpu, SensorType.Power),
            GpuLoad: gpu is null ? null
                : ByName(gpu, SensorType.Load, "GPU Core")
                  ?? FirstValue(gpu, SensorType.Load),
            RamLoad: memory is null ? null
                : ByName(memory, SensorType.Load, "Memory") // "Virtual Memory"가 아닌 물리 메모리
                  ?? FirstValue(memory, SensorType.Load, s => !s.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)),
            FanRpm: PickFan(fans),
            SsdTemp: ssdTemp);
    }

    private static void CollectFans(IHardware hardware, List<ISensor> fans)
    {
        foreach (var sensor in hardware.Sensors)
            if (sensor.SensorType == SensorType.Fan && sensor.Value is not null)
                fans.Add(sensor);
        foreach (var sub in hardware.SubHardware)
            CollectFans(sub, fans);
    }

    /// <summary>팬 선택: 이름에 CPU가 든 것 → 도는 것(&gt;0) → 아무거나.</summary>
    private static float? PickFan(List<ISensor> fans)
    {
        if (fans.Count == 0) return null;
        var cpuFan = fans.FirstOrDefault(f => f.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        var pick = cpuFan ?? fans.FirstOrDefault(f => f.Value > 0) ?? fans[0];
        return pick.Value;
    }

    /// <summary>이름 힌트(부분 일치, 순서대로)로 센서 하나를 찾는다.</summary>
    private static float? ByName(IHardware hardware, SensorType type, params string[] nameHints)
    {
        foreach (var hint in nameHints)
            foreach (var sensor in hardware.Sensors)
                if (sensor.SensorType == type && sensor.Value is not null
                    && sensor.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return sensor.Value;
        return null;
    }

    private static float? MaxValue(IHardware hardware, SensorType type, Func<ISensor, bool>? filter = null)
    {
        float? max = null;
        foreach (var sensor in hardware.Sensors)
            if (sensor.SensorType == type && sensor.Value is not null
                && (filter is null || filter(sensor))
                && (max is null || sensor.Value > max))
                max = sensor.Value;
        return max;
    }

    private static float? FirstValue(IHardware hardware, SensorType type, Func<ISensor, bool>? filter = null)
    {
        foreach (var sensor in hardware.Sensors)
            if (sensor.SensorType == type && sensor.Value is not null
                && (filter is null || filter(sensor)))
                return sensor.Value;
        return null;
    }

    /// <summary>표준 갱신 방문자 — 하드웨어 트리를 돌며 Update. Storage는 주기 제한 대상.</summary>
    private sealed class UpdateVisitor(bool includeStorage) : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            if (!includeStorage && hardware.HardwareType == HardwareType.Storage) return;
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
