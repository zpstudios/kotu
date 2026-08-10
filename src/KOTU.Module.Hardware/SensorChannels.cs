namespace KOTU.Module.Hardware;

/// <summary>
/// 센서 채널 하나의 표시 규격(A18에서 공용화). 뷰의 그래프 카드(A17)와 트레이 아이콘(A18)이
/// 같은 정의를 쓴다 — 제목·색·선택자·포맷이 두 곳에서 어긋나면 안 되기 때문.
/// </summary>
/// <param name="Id">설정 저장·선택 식별용 영구 ID (예: "cpuTemp"). 바꾸면 저장된 선택이 풀린다.</param>
/// <param name="Title">전체 표시명 — 트레이 툴팁·Copy all·카드 툴팁용.</param>
/// <param name="ShortTitle">카드 제목용 초단축 표기(v0.64.3 사용자 지시 — Temp·Power 같은
/// 단어 지양): 장치명 + 기호(°=온도, W=전력, %=부하, Clk=클럭). 전체 이름은 툴팁으로 보완.</param>
/// <param name="Accent">채널 색(대시보드 섹션 액센트 계열) — 카드 스파크라인·트레이 글자 색.</param>
/// <param name="Select">프레임에서 이 채널 값을 꺼낸다. null = 미가용.</param>
/// <param name="FormatFull">단위 포함 전체 표기 (카드 값·툴팁, 예: "62 °C").</param>
/// <param name="FormatCompact">트레이 아이콘용 초압축 표기 — 16px 안에 들어가야 한다 (예: "62°", "4.6").</param>
/// <param name="FixedMax">&gt;0이면 그래프 고정 스케일 상한 (온도·부하).</param>
/// <param name="AutoFloor">자동 스케일 시작 하한 (전력·클럭·팬).</param>
public sealed record SensorChannel(
    string Id, string Title, string ShortTitle, Windows.UI.Color Accent,
    Func<SensorFrame, float?> Select,
    Func<float, string> FormatFull,
    Func<float, string> FormatCompact,
    float FixedMax, float AutoFloor);

/// <summary>10채널 정의 단일 소스. 순서 = 뷰 카드 배치 순서(사용자 확정, v0.63.0 — v0.64.1부터 기본 1줄).</summary>
public static class SensorChannels
{
    private static readonly Windows.UI.Color Cpu = Windows.UI.Color.FromArgb(255, 0xE9, 0x60, 0x3D);
    private static readonly Windows.UI.Color Gpu = Windows.UI.Color.FromArgb(255, 0x7A, 0x5A, 0xF8);
    private static readonly Windows.UI.Color Ram = Windows.UI.Color.FromArgb(255, 0x2E, 0x9E, 0x6B);
    private static readonly Windows.UI.Color Fan = Windows.UI.Color.FromArgb(255, 0xC5, 0x8A, 0x00);
    private static readonly Windows.UI.Color Ssd = Windows.UI.Color.FromArgb(255, 0x3A, 0x7B, 0xD5);

    public static IReadOnlyList<SensorChannel> All { get; } =
    [
        new("cpuTemp", "CPU Temp", "CPU°", Cpu, f => f.CpuTemp, Celsius, CompactDegrees, FixedMax: 100, AutoFloor: 0),
        new("cpuPower", "CPU Power", "CPU W", Cpu, f => f.CpuPower, Watts, CompactWatts, FixedMax: 0, AutoFloor: 65),
        new("cpuLoad", "CPU Load", "CPU %", Cpu, f => f.CpuLoad, Percent, CompactPercent, FixedMax: 100, AutoFloor: 0),
        new("cpuClock", "CPU Clock", "CPU Clk", Cpu, f => f.CpuClock, Clock, CompactClock, FixedMax: 0, AutoFloor: 4000),
        new("gpuTemp", "GPU Temp", "GPU°", Gpu, f => f.GpuTemp, Celsius, CompactDegrees, FixedMax: 100, AutoFloor: 0),
        new("gpuPower", "GPU Power", "GPU W", Gpu, f => f.GpuPower, Watts, CompactWatts, FixedMax: 0, AutoFloor: 100),
        new("gpuLoad", "GPU Load", "GPU %", Gpu, f => f.GpuLoad, Percent, CompactPercent, FixedMax: 100, AutoFloor: 0),
        new("ram", "RAM", "RAM", Ram, f => f.RamLoad, Percent, CompactPercent, FixedMax: 100, AutoFloor: 0),
        new("fan", "Fan", "Fan", Fan, f => f.FanRpm, Rpm, CompactRpm, FixedMax: 0, AutoFloor: 1500),
        new("ssdTemp", "SSD Temp", "SSD°", Ssd, f => f.SsdTemp, Celsius, CompactDegrees, FixedMax: 100, AutoFloor: 0),
    ];

    /// <summary>ID로 채널을 찾는다. 미지의 ID(옛 설정 잔재 등)는 null.</summary>
    public static SensorChannel? ById(string id)
    {
        foreach (var channel in All)
            if (channel.Id == id) return channel;
        return null;
    }

    // ---------- 전체 표기 (카드 값·툴팁) ----------

    private static string Celsius(float v) => $"{v:0} °C";
    private static string Watts(float v) => $"{v:0} W";
    private static string Percent(float v) => $"{v:0} %";
    private static string Rpm(float v) => $"{v:0} RPM";
    private static string Clock(float v) => HardwareFormat.MegaHertz((uint)Math.Round(v));

    // ---------- 트레이 초압축 표기 (16px 폭 — 3글자 이내 목표) ----------

    /// <summary>두 자리까지는 단위 접미(°·W·%)를 붙이고, 세 자리부터는 숫자만 (폭 초과 방지).</summary>
    private static string CompactSuffixed(float v, string suffix)
        => v < 99.5f ? $"{v:0}{suffix}" : $"{v:0}";

    private static string CompactDegrees(float v) => CompactSuffixed(v, "°");
    private static string CompactWatts(float v) => CompactSuffixed(v, "W");
    private static string CompactPercent(float v) => CompactSuffixed(v, "%");

    /// <summary>클럭은 GHz 한 자리 소수 (4550 MHz → "4.6").</summary>
    private static string CompactClock(float v) => $"{v / 1000f:0.0}";

    /// <summary>팬은 999까지 그대로, 그 위는 k 표기 (1380 → "1.4k").</summary>
    private static string CompactRpm(float v) => v < 999.5f ? $"{v:0}" : $"{v / 1000f:0.0}k";
}
