using KOTU.Core.Settings;

namespace KOTU.Module.Hardware;

/// <summary>
/// 창(인스턴스)별 하드웨어 모듈 상태 + 전역 1벌 스토어(A70) — **저장 깔때기의 단일 소스**.
///
/// 설계(2026-08-13 사용자 확정, REQUIREMENTS 부록 B 42):
/// - **런타임에는 창마다 독립**: 각 HardwareView가 <see cref="CreateForView"/>로 자기 상태
///   (센서 선택 A18 · 채널 순서 A60 3차 · 하단 바 크기 A62)를 소유한다. 다른 창의 조작을 따라가지 않는다.
/// - **저장은 전역 1벌 · 마지막 조작 우선**: 모든 변경은 즉시 커밋 — 설정 키
///   (`hardware.traySensors`·`hardware.channelOrder`·`hardware.barScale`, 기존 이름 불변 =
///   마이그레이션 0)에 쓰고
///   전역 런타임 사본(마지막 커밋 1벌)을 갱신한다. 재실행·새 창의 초기값 = 그 1벌.
///   커밋 깔때기는 항목별로 하나씩 — **둘 다 무통지**: 선택 커밋의 통지(구 TraySensors.Changed →
///   SensorTray)는 A101(v0.137.0)에서 소비자와 함께 삭제됐고(창별 트레이 표시는 뷰가 자기
///   인스턴스 상태로 직접 통지한다), 바 크기는 A70부터 무통지(창 간 동기화 사양 제거).
/// - 변경(Toggle/StepBarScale)은 UI 스레드에서만 — 이 앱은 모든 창이 한 UI 스레드를 쓴다.
///
/// - A101(v0.137.0) 완료: 인스턴스 <see cref="Selection"/>을 HardwareView의 ITrayStatusProvider
///   구현이 읽어 창별 트레이 아이콘에 표시한다(전역 1벌은 저장·새 창 초기값 전용으로 남았다).
/// - A60 3차(v0.138.0) 완료: 채널 표시 순서(<see cref="Order"/> — 센터 그리드 드래그 재정렬)가
///   같은 원칙(인스턴스 필드 + 전역 1벌 `hardware.channelOrder` 무통지 커밋)으로 얹혔다.
///   선택(<see cref="Selection"/>)은 그대로 화면 선택 축을 겸한다 — 센터 타일 클릭 토글 하나로
///   핀 배지·좌 대형 그래프·하단 긴 그래프·창별 트레이가 전부 이 값을 따른다(선택 단일화).
/// </summary>
internal sealed class HardwareInstanceState
{
    // ---------- 전역 스토어 (static): 마지막 커밋 1벌 + 설정 로드/저장 ----------

    /// <summary>트레이 표시 센서 최대 개수(A18, 사용자 확정 2) — Toggle 밀어내기·뷰 트레이 표기 공용.</summary>
    internal const int MaxSelected = 2;

    private const string TraySensorsSettingKey = "hardware.traySensors"; // 채널 ID 콤마 목록
    private const string DefaultSelection = "cpuTemp,cpuPower"; // A18 기본(사용자 확정 — A60 3차 승계)

    /// <summary>채널 표시 순서(A60 3차) — 전 채널 ID 콤마 목록. 기본값(빈 문자열)은 정규화가
    /// SensorChannels.All 순서로 채운다.</summary>
    private const string ChannelOrderSettingKey = "hardware.channelOrder";

    /// <summary>
    /// 그래프 표시 크기 단계(A62): S / M(기본) / L = 0.85 / 1.0 / 1.25배 — 단계·설정 키는
    /// A237에서도 불변(마이그레이션 0). A62 원안은 A61 상시 표시 바의 가독성 목적이라 하단 바
    /// 안 요소 전용이었고, A237(v0.252.0)이 적용을 전 그래프 표면의 글씨·선 굵기로 확장했다
    /// (표면 크기 산식은 불변). 전역 UI 배율과는 별개의 배수다(구 A41 UiScale — 설정 콤보 전용).
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
    /// 전역(마지막 커밋) 선택 — 불변 스냅샷 배열. 워커 스레드 소비자(구 SensorTray의 ComposeKey)는
    /// A101에서 사라졌지만 "volatile 교체만, 제자리 수정 금지" 규약은 유지한다(A18부터의 계약 —
    /// 깨면 미래의 비 UI 스레드 소비자가 경합한다).
    /// </summary>
    private static volatile string[] _globalSelection = [];

    /// <summary>전역(마지막 커밋) 채널 순서(A60 3차) — Initialize/커밋의 정규화로 항상 전 채널을
    /// 정확히 1번씩 담는다. 불변 스냅샷 규약은 선택과 동일.</summary>
    private static volatile string[] _globalOrder = [];

    private static int _globalBarScaleIndex = DefaultBarScaleIndex; // UI 스레드에서만 접근

    // 구 GlobalSelection 노출 프로퍼티는 A101에서 제거 — 유일한 소비자가 TraySensors.Selected였다.
    // 새 창 초기값은 CreateForView가 _globalSelection 필드를 직접 복사한다.

    /// <summary>
    /// 앱 시작 시 1회(HardwareModule 생성) — 저장된 1벌을 전역 사본으로 복원한다.
    /// 미지의 채널 ID는 버리고(손으로 고친 settings.json 등), barScale은 목록 밖이면 M으로.
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
        _globalOrder = NormalizeOrder(settings.Get(ChannelOrderSettingKey, string.Empty));
        var storedScale = settings.Get(BarScaleSettingKey, BarScaleSteps[DefaultBarScaleIndex].Id);
        var scaleIndex = Array.FindIndex(BarScaleSteps, step => step.Id == storedScale);
        _globalBarScaleIndex = scaleIndex >= 0 ? scaleIndex : DefaultBarScaleIndex;
    }

    /// <summary>
    /// 저장된 순서 정규화(A60 3차): 미지 ID(옛 설정 잔재·손으로 고친 settings.json)는 버리고,
    /// 빠진 채널은 기본 순서(<see cref="SensorChannels.All"/>)대로 뒤에 보충한다 — 채널 개편
    /// (추가·삭제·개명) 뒤에도 전 채널이 정확히 1번씩 있는 순서가 보장된다(채널 개편 내성).
    /// </summary>
    private static string[] NormalizeOrder(string raw)
    {
        var order = new List<string>();
        foreach (var id in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (SensorChannels.ById(id) is not null && !order.Contains(id))
                order.Add(id);
        foreach (var channel in SensorChannels.All)
            if (!order.Contains(channel.Id))
                order.Add(channel.Id);
        return [.. order];
    }

    /// <summary>
    /// 선택 커밋 깔때기 — `hardware.traySensors`를 쓰는 유일한 곳. 전역 사본 교체 → 설정 저장.
    /// 통지 없음(A101): 구 TraySensors.Changed는 유일 소비자(SensorTray)와 함께 삭제됐다 —
    /// 창별 트레이 표시는 뷰가 자기 인스턴스 상태에서 직접 통지한다(전역 1벌 = 저장 전용).
    /// 전부 해제 경로였던 구 트레이 메뉴("Hide tray sensors" → ClearGlobalSelection)도 함께
    /// 소멸 — 빈 커밋은 이제 열린 창에서 핀을 모두 끌 때만 나온다.
    /// </summary>
    private static void CommitSelection(string[] snapshot)
    {
        _globalSelection = snapshot;
        if (_settings is not null)
        {
            _settings.Set(TraySensorsSettingKey, string.Join(',', snapshot));
            _settings.Save();
        }
    }

    /// <summary>
    /// 순서 커밋 깔때기(A60 3차) — `hardware.channelOrder`를 쓰는 유일한 곳. 무통지(바 크기와
    /// 같은 원칙 — 창 간 동기화 없음, A70). 호출은 드롭 확정 시 1회(<see cref="MoveTo"/>)뿐이라
    /// 드래그 중 설정 Save 난사가 없다.
    /// </summary>
    private static void CommitOrder(string[] snapshot)
    {
        _globalOrder = snapshot;
        if (_settings is not null)
        {
            _settings.Set(ChannelOrderSettingKey, string.Join(',', snapshot));
            _settings.Save();
        }
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
    private readonly List<string> _order; // 채널 표시 순서(A60 3차) — 항상 전 채널(정규화 보장)
    private volatile string[] _orderSnapshot;
    private int _barScaleIndex;

    private HardwareInstanceState(List<string> selected, List<string> order, int barScaleIndex)
    {
        _selected = selected;
        _selectionSnapshot = [.. selected];
        _order = order;
        _orderSnapshot = [.. order];
        _barScaleIndex = barScaleIndex;
    }

    /// <summary>새 뷰용 인스턴스 상태 — 전역 현재값(마지막 커밋 1벌)의 복사로 시작한다.</summary>
    internal static HardwareInstanceState CreateForView()
        => new([.. _globalSelection], [.. _globalOrder], _globalBarScaleIndex);

    /// <summary>이 창의 선택(선택한 순서). 불변 스냅샷 규약(전역 사본과 동일) — A101의 트레이
    /// 표기(GetTrayStatus·NotifyTrayStatus)는 UI 스레드에서 읽지만, 어느 스레드가 열거해도 안전하다.</summary>
    internal IReadOnlyList<string> Selection => _selectionSnapshot;

    internal bool IsSelected(string id) => Array.IndexOf(_selectionSnapshot, id) >= 0;

    /// <summary>
    /// 타일 클릭 토글(A60 3차 — 구 카드 클릭의 후신) — A18 규칙 그대로: 미지 ID 무시, 켤 때 이미
    /// 가득(2개)이면 가장 오래된 선택이 밀려난다(대화상자 없이 즉시). 변경 즉시 전역 1벌로 커밋
    /// (마지막 조작 우선).
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

    /// <summary>이 창의 채널 표시 순서(A60 3차 — 센터 그리드·Copy all이 따른다). 전 채널을 정확히
    /// 1번씩 담는다(Initialize/CreateForView의 정규화 보장). 불변 스냅샷 규약은 선택과 동일.</summary>
    internal IReadOnlyList<string> Order => _orderSnapshot;

    /// <summary>
    /// 드래그 재정렬(A60 3차): id를 뽑아 targetId 자리에 끼운다 — RemoveAt 뒤 원래의 타깃
    /// 인덱스를 그대로 써서, 앞→뒤 드래그는 타깃 뒤에·뒤→앞 드래그는 타깃 앞에 놓인다
    /// (이동 방향으로 밀어내는 통상의 재정렬 UX). 미지 ID·제자리 드롭은 무동작.
    /// 변경 즉시 전역 1벌로 커밋(마지막 조작 우선) — 커밋은 드롭당 1회다.
    /// </summary>
    internal void MoveTo(string id, string targetId)
    {
        var from = _order.IndexOf(id);
        var to = _order.IndexOf(targetId);
        if (from < 0 || to < 0 || from == to) return;
        _order.RemoveAt(from);
        _order.Insert(to, id);
        _orderSnapshot = [.. _order];
        CommitOrder(_orderSnapshot);
    }

    /// <summary>이 창의 현재 배수(0.85 / 1.0 / 1.25).</summary>
    internal double BarScale => BarScaleSteps[_barScaleIndex].Factor;

    /// <summary>
    /// 단계 스텝(A237 — 구 CycleBarScale 순환의 후신): step +1 = 확대·-1 = 축소, 끝에서 클램프
    /// (S에서 -, L에서 +는 무동작 — 랩 금지). step 0 = 기본 단계(M) 리셋(Ctrl+넘패드 '*' —
    /// 문서 모듈 100% 리셋과 대칭). 휠은 여러 칸이 한 번에 올 수 있어 ±1 초과도 받는다.
    /// 결과가 그대로면 커밋(설정 Save)도 생략한다 — 끝 단계에서 키를 꾹 눌러도(오토리피트)
    /// 저장 난사가 없다. 이 창만 바뀌고 저장은 전역 1벌(A70)인 것은 종전 그대로.
    /// </summary>
    internal void StepBarScale(int step)
    {
        var next = step == 0
            ? DefaultBarScaleIndex
            : Math.Clamp(_barScaleIndex + step, 0, BarScaleSteps.Length - 1);
        if (next == _barScaleIndex) return;
        _barScaleIndex = next;
        CommitBarScale(next);
    }
}
