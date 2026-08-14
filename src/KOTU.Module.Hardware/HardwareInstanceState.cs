using KOTU.Core.Settings;

namespace KOTU.Module.Hardware;

/// <summary>
/// 창(인스턴스)별 하드웨어 모듈 상태 + 전역 1벌 스토어(A70) — **저장 깔때기의 단일 소스**.
///
/// 설계(2026-08-13 사용자 확정, REQUIREMENTS 부록 B 42):
/// - **런타임에는 창마다 독립**: 각 HardwareView가 <see cref="CreateForView"/>로 자기 상태
///   (센서 선택 A18 · 하단 바 크기 A62)를 소유한다. 다른 창의 조작을 따라가지 않는다.
/// - **저장은 전역 1벌 · 마지막 조작 우선**: 모든 변경은 즉시 커밋 — 설정 키
///   (`hardware.traySensors`·`hardware.barScale`, 이름 불변 = 마이그레이션 0)에 쓰고
///   전역 런타임 사본(마지막 커밋 1벌)을 갱신한다. 재실행·새 창의 초기값 = 그 1벌.
///   커밋 깔때기는 항목별로 하나씩 — 선택 커밋만 <see cref="TraySensors.Changed"/>를 쏜다
///   (SensorTray 소비). 바 크기 커밋은 무통지: 창 간 동기화가 사양에서 제거됐고(A70)
///   트레이는 바 크기를 쓰지 않는다.
/// - 변경(Toggle/CycleBarScale)은 UI 스레드에서만 — 이 앱은 모든 창이 한 UI 스레드를 쓴다.
///
/// 뒤 배치가 얹힐 훅:
/// - **A60 3차**(피닝 목록·그래프 순서·대형 그래프 모드)의 창별 상태는 여기 인스턴스 측에
///   필드를 추가한다 — 저장 깔때기(전역 1벌·마지막 커밋)는 새 필드에도 같은 원칙.
/// - **A101**(A18 센서 아이콘 폐지 → 창별 트레이 아이콘이 그 창 선택값 표시)은 인스턴스
///   <see cref="Selection"/>을 창별 ITrayStatusProvider 경로로 읽는다 — HardwareView의
///   ITrayStatusProvider 구현은 A101의 일(지금 H/W 유휴 표기는 셸 상수 INF).
/// </summary>
internal sealed class HardwareInstanceState
{
    // ---------- 전역 스토어 (static): 마지막 커밋 1벌 + 설정 로드/저장 ----------

    /// <summary>트레이 표시 센서 최대 개수(A18, 사용자 확정 2). TraySensors.MaxCount가 노출한다.</summary>
    internal const int MaxSelected = 2;

    private const string TraySensorsSettingKey = "hardware.traySensors"; // 채널 ID 콤마 목록
    private const string DefaultSelection = "cpuTemp,cpuPower"; // A18 기본(사용자 확정)

    /// <summary>
    /// 하단 바 표시 크기 단계(A62): S / M(기본) / L = 0.85 / 1.0 / 1.25배.
    /// A61의 상시 표시 바에서 가독성을 확보하는 것이 목적이라 **하단 바 안 요소에만** 곱한다 —
    /// 전역 UI 배율(A41)과는 별개의 배수다(UiScale은 건드리지 않는다).
    /// 단계 표는 상수라 프로세스 공유가 맞다 — A70에서 HardwareModule에서 여기로 이관
    /// (barScale 상태의 나머지 전부가 이 파일로 왔으므로 표만 남기지 않았다).
    /// </summary>
    internal static readonly (string Id, string Label, double Factor)[] BarScaleSteps =
    [
        ("S", "Small", 0.85),
        ("M", "Medium", 1.0),
        ("L", "Large", 1.25),
    ];

    /// <summary>기본 단계 M의 인덱스 — 저장값이 목록 밖일 때도 여기로 떨어진다(A62).</summary>
    private const int DefaultBarScaleIndex = 1;

    private const string BarScaleSettingKey = "hardware.barScale";

    private static ISettingsService? _settings; // Initialize에서 주입 — 커밋 깔때기 저장용

    /// <summary>
    /// 전역(마지막 커밋) 선택 — 불변 스냅샷 배열. SensorTray의 ComposeKey가 **워커 스레드**에서
    /// 열거하므로 volatile 교체만 하고 절대 제자리 수정하지 않는다(A18 규약 유지).
    /// </summary>
    private static volatile string[] _globalSelection = [];

    private static int _globalBarScaleIndex = DefaultBarScaleIndex; // UI 스레드에서만 접근

    /// <summary>전역(마지막 커밋) 선택 — TraySensors.Selected가 그대로 노출한다.</summary>
    internal static IReadOnlyList<string> GlobalSelection => _globalSelection;

    /// <summary>
    /// 앱 시작 시 1회(HardwareModule 생성) — 저장된 1벌을 전역 사본으로 복원한다.
    /// 미지의 채널 ID는 버리고(손으로 고친 settings.json 등), barScale은 목록 밖이면 M으로.
    /// Changed는 쏘지 않는다 — 구독자(SensorTray)가 붙기 전이고, SensorTray가 생성 시 직접 읽는다.
    /// </summary>
    internal static void Initialize(ISettingsService settings)
    {
        _settings = settings;
        var selected = new List<string>();
        var raw = settings.Get(TraySensorsSettingKey, DefaultSelection);
        foreach (var id in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (SensorChannels.ById(id) is not null && !selected.Contains(id) && selected.Count < MaxSelected)
                selected.Add(id);
        _globalSelection = [.. selected];
        var storedScale = settings.Get(BarScaleSettingKey, BarScaleSteps[DefaultBarScaleIndex].Id);
        var scaleIndex = Array.FindIndex(BarScaleSteps, step => step.Id == storedScale);
        _globalBarScaleIndex = scaleIndex >= 0 ? scaleIndex : DefaultBarScaleIndex;
    }

    /// <summary>
    /// 트레이 메뉴 "Hide tray sensors"(TraySensors.Clear 경유) — **전역 1벌만 비운다**(A70).
    /// 열려 있는 창들의 런타임 선택은 그대로 남는다(허용된 발산): 다음에 어느 창에서든 핀을
    /// 토글하면 그 창의 선택 전체가 다시 커밋되어 아이콘이 되살아난다.
    /// </summary>
    internal static void ClearGlobalSelection()
    {
        if (_globalSelection.Length == 0) return; // 이미 비어 있으면 쓰기·통지 생략(현행 유지)
        CommitSelection([]);
    }

    /// <summary>
    /// 선택 커밋 깔때기 — `hardware.traySensors`를 쓰는 유일한 곳. 전역 사본 교체 → 설정 저장 →
    /// TraySensors.Changed(UI 스레드 동기 — SensorTray가 구독·아이콘을 맞춘다). 핸들러는 읽기만
    /// 한다는 것이 전제 — 핸들러 안에서 재커밋(재진입)하는 경로를 만들지 말 것.
    /// </summary>
    private static void CommitSelection(string[] snapshot)
    {
        _globalSelection = snapshot;
        if (_settings is not null)
        {
            _settings.Set(TraySensorsSettingKey, string.Join(',', snapshot));
            _settings.Save();
        }
        TraySensors.RaiseChanged();
    }

    /// <summary>바 크기 커밋 깔때기 — `hardware.barScale`을 쓰는 유일한 곳. 무통지(클래스 헤더 참조).</summary>
    private static void CommitBarScale(int index)
    {
        _globalBarScaleIndex = index;
        if (_settings is not null)
        {
            _settings.Set(BarScaleSettingKey, BarScaleSteps[index].Id);
            _settings.Save();
        }
    }

    // ---------- 인스턴스 상태: 창(HardwareView) 하나가 소유 ----------

    private readonly List<string> _selected; // 선택 순서 유지(오래된 것부터 밀려난다)
    private volatile string[] _selectionSnapshot; // 불변 스냅샷 — 전역 사본과 같은 규약
    private int _barScaleIndex;

    private HardwareInstanceState(List<string> selected, int barScaleIndex)
    {
        _selected = selected;
        _selectionSnapshot = [.. selected];
        _barScaleIndex = barScaleIndex;
    }

    /// <summary>새 뷰용 인스턴스 상태 — 전역 현재값(마지막 커밋 1벌)의 복사로 시작한다.</summary>
    internal static HardwareInstanceState CreateForView() => new([.. _globalSelection], _globalBarScaleIndex);

    /// <summary>이 창의 선택(선택한 순서). 불변 스냅샷 — A101이 워커 스레드에서 읽어도 안전하게.</summary>
    internal IReadOnlyList<string> Selection => _selectionSnapshot;

    internal bool IsSelected(string id) => Array.IndexOf(_selectionSnapshot, id) >= 0;

    /// <summary>
    /// 카드 클릭 토글 — A18 규칙 그대로: 미지 ID 무시, 켤 때 이미 가득(2개)이면 가장 오래된
    /// 선택이 밀려난다(대화상자 없이 즉시). 변경 즉시 전역 1벌로 커밋(마지막 조작 우선).
    /// </summary>
    internal void Toggle(string id)
    {
        if (SensorChannels.ById(id) is null) return;
        if (!_selected.Remove(id))
        {
            while (_selected.Count >= MaxSelected) _selected.RemoveAt(0);
            _selected.Add(id);
        }
        _selectionSnapshot = [.. _selected];
        CommitSelection(_selectionSnapshot);
    }

    /// <summary>현재 단계 인덱스(<see cref="BarScaleSteps"/>) — 버튼 툴팁 표기에 쓴다.</summary>
    internal int BarScaleIndex => _barScaleIndex;

    /// <summary>이 창의 현재 배수(0.85 / 1.0 / 1.25).</summary>
    internal double BarScale => BarScaleSteps[_barScaleIndex].Factor;

    /// <summary>다음 단계로 순환(S → M → L → S) + 전역 1벌 커밋. 이 창만 바뀐다(A70).</summary>
    internal void CycleBarScale()
    {
        _barScaleIndex = (_barScaleIndex + 1) % BarScaleSteps.Length;
        CommitBarScale(_barScaleIndex);
    }
}
