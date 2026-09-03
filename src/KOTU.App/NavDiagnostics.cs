using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App;

/// <summary>
/// 폴더 항해 계측판 — 설정의 숨김 토글(diag.navTiming, 기본 꺼짐)로만 켜진다.
/// 토글 관용구(SettingKey + Changed + NotifyChanged)는 ShellDiagnostics(A234)·
/// EditorDecorDiagnostics(A285) 복제, 표시 스트립은 MainWindow가 소유한다(NavDiagStrip).
/// <para>
/// 계측 목적: "대용량 폴더 진입 시 몇 초 정지"에 대한 추측 수리가 두 번(A333 포함) 빗나갔다.
/// 사용자 관찰 — 진입 직후 창 크기 조절이 stuck되고 그때 비로소 "Loading..."이 보인다 —
/// 은 UI 스레드 자체가 막힌다는 뜻이지만, 어느 구간에서 막히는지는 추측뿐이었다.
/// 그래서 이 시설은 고치지 않고 **잰다**: 스크린샷 한 장으로 ⓐ 마일스톤 사이 경과 ms와
/// ⓑ UI 스레드가 메시지 루프로 돌아오지 못한 최대 간격(그리고 그 간격이 어느 구간에서
/// 났는지)을 함께 읽을 수 있게 한다.
/// </para>
/// <para>
/// 비용 계약: 꺼짐이면 모든 진입점이 <see cref="Enabled"/> 한 번 읽고 즉시 반환한다
/// (하트비트 타이머는 아예 만들어지지도 않는다 — MainWindow.ApplyNavDiagnostics).
/// 켜져 있어도 마크는 <see cref="Stopwatch.GetTimestamp"/> 한 번 + 배열 대입뿐이고,
/// 문자열 조립은 <see cref="Publish"/> 한 곳에서만 한다(계측이 대상을 왜곡하지 않게).
/// </para>
/// <para>
/// 상태는 static 1벌이다 — 창이 여럿이면 마지막으로 항해한 창의 값이 모든 창의 스트립에
/// 보인다(진단 전용, 실사용 재현은 단일 창이라 수용). 다만 하트비트만은 스레드별로 따로
/// 잰다(<c>[ThreadStatic]</c> _lastBeat) — 창마다 자기 UI 스레드에서 타이머가 돌기 때문에
/// 공유 필드로 재면 다른 창의 틱이 간격을 메워 정지를 가려 버린다.
/// </para>
/// </summary>
public static class NavDiagnostics
{
    /// <summary>설정 키. 값은 bool, 기본 false — 진단 전용이라 일반 사용자에게 보이지 않는다.
    /// 파일(settings.json)에 저장되므로 재시작 후에도 유지된다(ShellDiagnostics와 같은 결정).</summary>
    public const string SettingKey = "diag.navTiming";

    /// <summary>설정 변경 시 열린 모든 창이 스트립 표시를 다시 적용하도록 알린다(설정 화면 → 각 MainWindow).</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>계측 값이 새로 조립됐다 — 스트립을 다시 그리라는 신호(MainWindow가 구독).
    /// 발화 스레드는 항해가 돈 UI 스레드라, 구독자는 자기 창의 큐로 마샬링해야 한다.</summary>
    public static event Action? Updated;

    /// <summary>한 항해에서 기록할 수 있는 마크 상한 — 현행 마크는 14개다(여유 포함 고정값).
    /// 넘치면 조용히 버린다(진단이 대상을 방해하지 않는 쪽이 우선).</summary>
    private const int MaxMarks = 24;

    /// <summary>하트비트 새 최대값이 났을 때의 재조립 최소 간격(ms) — 매 틱 문자열을 만들지
    /// 않으려는 스로틀이다. 이 값보다 자주 새 최대값이 나도 조립은 한 번만 한다.</summary>
    private const long RepublishMinMs = 250;

    private static readonly object Gate = new();
    private static readonly string[] Names = new string[MaxMarks];
    private static readonly long[] Stamps = new long[MaxMarks];

    private static long _session;              // 항해 일련번호 — 지연 마크(MarkFor)의 귀속 판정용
    private static int _count;                 // 이번 항해에 기록된 마크 수(0 = 없음)
    private static bool _active;               // Begin 이후인가 — 다음 Begin까지 참으로 남는다
    private static string _source = "-";       // 항해를 시작시킨 표면(tree/list/grid/other)
    private static string? _pendingSource;     // 진입점이 미리 적어 둔 출처 — 다음 Begin이 소비
    private static int _navThread;             // 항해가 돌고 있는 UI 스레드(하트비트 귀속 판정)

    private static long _stallStamp;           // 관측된 최대 하트비트 간격(틱)
    private static int _stallAfter = -1;       // 그 간격이 난 시점의 직전 마크 인덱스(-1 = 시작 전)
    private static long _lastPublish;          // 마지막 조립 시각(틱) — RepublishMinMs 스로틀 기준

    // A342 축 C — 분할 조립 루프의 틱 계측(L = 좌 리스트, C = 중앙 타일).
    // 각 루프마다 body(틱 핸들러 본문 소요)와 gap(직전 틱 시작 → 이번 틱 시작)의 최대값과
    // 그 값이 난 틱의 마지막 항목 인덱스를 따로 들고 있는다. At가 음수면 "기록 없음"이다.
    private static long _tickLBody;
    private static int _tickLBodyAt = -1;
    private static long _tickLGap;
    private static int _tickLGapAt = -1;
    private static long _tickCBody;
    private static int _tickCBodyAt = -1;
    private static long _tickCGap;
    private static int _tickCGapAt = -1;

    /// <summary>스레드별 마지막 하트비트 시각(틱) — 위 클래스 주석의 "창마다 따로" 근거.</summary>
    [ThreadStatic] private static long _lastBeat;

    private static bool _paintPending;                  // 렌더 1프레임 구독이 살아 있는가
    private static string _paintName = "paint";

    private static string _segmentsLine = string.Empty;
    private static string _stallLine = string.Empty;

    /// <summary>진단 켜짐 — 모든 진입점의 앞단 게이트. 갱신은 <see cref="SetEnabled"/> 한 곳이다.
    /// volatile: 워커가 아니라 창마다 다른 UI 스레드가 읽고 쓴다(설정 화면이 다른 창일 수 있다).</summary>
    private static volatile bool _enabled;

    public static bool Enabled => _enabled;

    /// <summary>지금 항해의 일련번호 — 큐에 넣어 두는 지연 마크(<see cref="MarkFor"/>)가
    /// 자기 항해에만 기록되도록 붙잡아 두는 표다. 연속 항해(빠른 두 번 클릭)에서 앞 항해의
    /// 지연 콜백이 뒤 항해의 마크 자리를 차지하는 사고를 막는다.</summary>
    public static long Session
    {
        get { lock (Gate) return _session; }
    }

    /// <summary>스트립 1줄 = 구간별 경과 ms. 최대 구간은 대괄호로 감싼다.</summary>
    public static string SegmentsLine
    {
        get { lock (Gate) return _segmentsLine; }
    }

    /// <summary>스트립 2줄 = UI 스레드 최대 정지와 그 구간(이 한 줄이 이번 계측의 결론이다).</summary>
    public static string StallLine
    {
        get { lock (Gate) return _stallLine; }
    }

    /// <summary>토글 반영(MainWindow.ApplyNavDiagnostics 전용). 끄면 누적을 통째로 비운다 —
    /// 다시 켰을 때 낡은 값이 남아 오해를 부르지 않게.</summary>
    public static void SetEnabled(bool on)
    {
        _enabled = on;
        if (on) return;
        lock (Gate)
        {
            _active = false;
            _count = 0;
            _pendingSource = null;
            _stallStamp = 0;
            _stallAfter = -1;
            ResetTicksLocked(); // A342 — 틱 계측도 함께 비운다(낡은 값이 남지 않게)
            _segmentsLine = string.Empty;
            _stallLine = string.Empty;
        }
    }

    /// <summary>A342: 틱 계측 누적 리셋 — Begin(새 항해)과 SetEnabled(false) 두 곳이 부른다.</summary>
    private static void ResetTicksLocked()
    {
        _tickLBody = 0;
        _tickLBodyAt = -1;
        _tickLGap = 0;
        _tickLGapAt = -1;
        _tickCBody = 0;
        _tickCBodyAt = -1;
        _tickCGap = 0;
        _tickCGapAt = -1;
    }

    /// <summary>
    /// 항해 진입점이 "누가 시작했는지"를 적어 둔다 — 곧바로 이어지는 <see cref="Begin"/>이 소비한다.
    /// 공개 API(ExplorerPane.NavigateTo)의 시그니처를 건드리지 않으려는 장치다: 세 진입 경로
    /// (트리 선택·리스트 활성화·중앙 썸네일 더블클릭)는 전부 그 한 메서드로 합류하는데,
    /// 합류점에서는 출처를 알 수 없기 때문이다. 적어 두지 않은 경로는 "other"로 찍힌다.
    /// </summary>
    public static void NoteSource(string source)
    {
        if (!_enabled) return;
        lock (Gate) _pendingSource = source;
    }

    /// <summary>
    /// A323 경로의 오해 방지: 같은 폴더·같은 필터라 재항해 자체를 생략했다는 표시.
    /// 출처가 적혀 있을 때만 발화한다 — FileListOverlay.Show는 모드 전환마다 불리므로,
    /// 사용자가 실제로 항해를 지시한 경우가 아니면 스트립을 덮어쓰지 않는다.
    /// </summary>
    public static void NoteSkipped()
    {
        if (!_enabled) return;
        lock (Gate)
        {
            if (_pendingSource is null) return;
        }
        Begin();
        Mark("skip");
        Publish();
    }

    /// <summary>새 항해 시작 — 누적을 리셋하고 첫 마크(nav)를 찍는다. 호출은 합류점 한 곳
    /// (ExplorerPane.NavigateTo)이라 세 진입 경로가 모두 같은 계측을 탄다.</summary>
    public static void Begin()
    {
        if (!_enabled) return;
        var now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            _source = _pendingSource ?? "other";
            _pendingSource = null;
            _session++;
            _count = 1;
            Names[0] = "nav";
            Stamps[0] = now;
            _active = true;
            _navThread = Environment.CurrentManagedThreadId;
            _stallStamp = 0;
            _stallAfter = -1;
            ResetTicksLocked(); // A342 — 항해마다 틱 최대값을 새로 잰다
            _lastPublish = now;
        }
        // 하트비트 기준점도 여기서 다시 잡는다 — 직전 틱이 언제였든 이번 항해의 첫 구간은
        // "Begin 이후"부터 재는 것이 맞다(이 호출 자체가 UI 스레드다).
        _lastBeat = now;
    }

    /// <summary>마일스톤 1개 기록. 같은 이름은 이번 항해에서 처음 것만 남는다 —
    /// 정렬 클릭·필터 토글이 항해 없이 Fill을 다시 돌려도 낡은 세션에 덧붙지 않게 하는 장치다.</summary>
    public static void Mark(string name)
    {
        if (!_enabled) return;
        var now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            if (!_active || _count >= MaxMarks) return;
            for (var i = 0; i < _count; i++)
                if (string.Equals(Names[i], name, StringComparison.Ordinal))
                    return;
            Names[_count] = name;
            Stamps[_count] = now;
            _count++;
        }
    }

    /// <summary>지연 마크 — 큐에 넣어 두었다가 나중에 도는 콜백용(현행 사용처 = yield 한 곳).
    /// 그 사이에 다른 항해가 시작됐으면 조용히 버린다.</summary>
    public static void MarkFor(long session, string name)
    {
        if (!_enabled) return;
        lock (Gate)
        {
            if (_session != session) return;
        }
        Mark(name);
    }

    /// <summary>
    /// A342 축 C — 분할 조립 루프(좌 리스트·중앙 타일)의 틱 1회 계측.
    /// 배경: 정지 라인이 prev0&gt;fillN 구간을 가리키면서도 미리보기 개수와 무관하게 487ms로
    /// 불변이었다(v0.326.0 → v0.327.0). 그 구간의 어느 틱이 정지의 주인인지 틱 단위로 좁힌다.
    /// <para>
    /// body = 틱 핸들러 본문 소요, gap = 같은 루프의 직전 틱 시작부터 이번 틱 시작까지.
    /// gap을 함께 재는 이유: body만 재면 핸들러가 돌아온 뒤 XAML이 도는 measure/arrange/render
    /// 비용이 통째로 빠진다(80개 ListViewItem을 붙인 레이아웃 패스가 바로 그것이고, 정지의
    /// 주인이 거기일 수 있다). gap이 크고 body가 작으면 레이아웃·외부 원인, 둘 다 크면 조립 자체다.
    /// </para>
    /// <para>
    /// 한계(수용): 두 루프가 같은 렌더 프레임에서 연달아 불릴 수 있어 L의 gap에 C의 body가
    /// 섞인다. 해석할 때 감안한다 — 걷어내려면 프레임 단위 상관 계측이 따로 필요하다.
    /// </para>
    /// </summary>
    /// <param name="loop">'L' = 좌 리스트(ExplorerPane), 'C' = 중앙 타일(ThumbnailExplorer).</param>
    /// <param name="lastIndex">그 틱이 붙인 마지막 항목 인덱스.</param>
    /// <param name="bodyTicks">틱 핸들러 본문 소요(Stopwatch 틱).</param>
    /// <param name="gapTicks">직전 틱 시작부터 이번 틱 시작까지(첫 틱은 0 — 무시한다).</param>
    public static void NoteTick(char loop, int lastIndex, long bodyTicks, long gapTicks)
    {
        if (!_enabled) return;
        lock (Gate)
        {
            if (!_active) return;
            if (loop == 'L')
            {
                if (_tickLBodyAt < 0 || bodyTicks > _tickLBody)
                {
                    _tickLBody = bodyTicks;
                    _tickLBodyAt = lastIndex;
                }
                if (gapTicks > 0 && (_tickLGapAt < 0 || gapTicks > _tickLGap))
                {
                    _tickLGap = gapTicks;
                    _tickLGapAt = lastIndex;
                }
            }
            else if (loop == 'C')
            {
                if (_tickCBodyAt < 0 || bodyTicks > _tickCBody)
                {
                    _tickCBody = bodyTicks;
                    _tickCBodyAt = lastIndex;
                }
                if (gapTicks > 0 && (_tickCGapAt < 0 || gapTicks > _tickCGap))
                {
                    _tickCGap = gapTicks;
                    _tickCGapAt = lastIndex;
                }
            }
        }
    }

    /// <summary>
    /// 축 B — UI 스레드 하트비트 1틱. MainWindow의 DispatcherTimer가 켜져 있는 동안만 부른다.
    /// 연속 틱 사이의 간격이 곧 "UI 스레드가 메시지 루프로 돌아오지 못한 시간"이다.
    /// 조립은 여기서 하지 않는다(스로틀에 걸린 경우 제외) — 계측이 대상을 왜곡하지 않게.
    /// </summary>
    public static void Beat()
    {
        if (!_enabled) return;
        var now = Stopwatch.GetTimestamp();
        var prev = _lastBeat;
        _lastBeat = now;
        if (prev == 0) return; // 이 스레드의 첫 틱 — 기준점만 잡고 끝
        var gap = now - prev;
        var republish = false;
        lock (Gate)
        {
            // 항해가 돌고 있는 UI 스레드의 틱만 센다 — 다른 창의 틱은 이 구간의 증거가 아니다.
            if (!_active || Environment.CurrentManagedThreadId != _navThread) return;
            if (gap <= _stallStamp) return;
            _stallStamp = gap;
            _stallAfter = _count - 1;
            republish = _count > 1 && ToMs(now - _lastPublish) >= RepublishMinMs;
        }
        // 새 최대값이 났고 마지막 조립에서 충분히 지났을 때만 다시 그린다 — 렌더 프레임을
        // 기다리지 못하고 끝나는 항해(예외·취소)에서도 최대 정지가 화면에 남게 하는 안전망이다.
        if (republish) Publish();
    }

    /// <summary>
    /// 반영이 끝난 뒤 **첫 렌더 프레임**에서 마크 하나를 찍고 조립한다 — "화면에 실제로
    /// 그려진 시각"은 Items 조작 직후가 아니라 다음 프레임이기 때문이다.
    /// CompositionTarget.Rendering 1회 구독 관용구는 저장소 선례(A192 ExplorerPane·
    /// ThumbnailExplorer의 분할 조립 루프)와 같은 형태이고, 첫 틱에서 반드시 해제한다.
    /// 이미 대기 중이면 무동작 — 좌 리스트와 중앙 썸네일이 각자 무장하므로 순서만 밀린다.
    /// </summary>
    public static void ArmPaint(string name)
    {
        if (!_enabled) return;
        lock (Gate)
        {
            if (!_active || _paintPending) return;
            _paintName = name;
            _paintPending = true;
        }
        CompositionTarget.Rendering += OnPaintTick;
    }

    private static void OnPaintTick(object? sender, object? e)
    {
        // static 이벤트라 예외가 새면 앱 전역 크래시 — 본문 전체를 감싼다(A192 틱 핸들러와 같은 이유).
        try
        {
            CompositionTarget.Rendering -= OnPaintTick;
            string name;
            lock (Gate)
            {
                _paintPending = false;
                name = _paintName;
            }
            Mark(name);
            Publish();
        }
        catch (Exception)
        {
            // 계측은 실패해도 앱 동작에 영향을 주지 않는다 — 조용히 버린다.
        }
    }

    /// <summary>문자열 조립의 유일한 지점(마크 기록은 값만 남긴다는 계약의 반쪽).</summary>
    private static void Publish()
    {
        if (!_enabled) return;
        lock (Gate)
        {
            BuildLocked();
            _lastPublish = Stopwatch.GetTimestamp();
        }
        Updated?.Invoke();
    }

    private static void BuildLocked()
    {
        if (_count == 0)
        {
            _segmentsLine = string.Empty;
            _stallLine = string.Empty;
            return;
        }

        // 최대 구간 먼저 찾아 둔다 — 그 한 칸만 대괄호로 감싸 눈에 띄게 한다.
        var big = -1;
        long bigGap = -1;
        for (var i = 1; i < _count; i++)
        {
            var gap = Stamps[i] - Stamps[i - 1];
            if (gap <= bigGap) continue;
            bigGap = gap;
            big = i;
        }

        var skipped = string.Equals(Names[_count - 1], "skip", StringComparison.Ordinal);

        var text = new StringBuilder();
        text.Append("NAV ").Append(_source);
        for (var i = 1; !skipped && i < _count; i++)
        {
            text.Append(" · ");
            if (i == big) text.Append('[');
            text.Append(Names[i - 1]).Append('>').Append(Names[i])
                .Append(' ').Append(ToMs(Stamps[i] - Stamps[i - 1]));
            if (i == big) text.Append(']');
        }
        if (skipped)
            text.Append(" · no rescan (same folder)"); // A323 경로 — 재탐색 자체가 없었다는 표시
        else if (_count > 1)
            text.Append(" · total ").Append(ToMs(Stamps[_count - 1] - Stamps[0])).Append("ms");
        _segmentsLine = text.ToString();

        _stallLine = _stallStamp <= 0
            ? "UI stall max not observed"
            : $"UI stall max {ToMs(_stallStamp)}ms @{SegmentNameLocked(_stallAfter)}";

        // A342: 정지 라인 뒤에 틱 계측을 덧붙인다 — 정지가 "not observed"여도 붙인다
        // (틱 쪽에만 증거가 남는 경우가 있기 때문). 조립은 여기 한 곳뿐이라는 계약 그대로다.
        var ticks = new StringBuilder(" · tick ");
        AppendTickLocked(ticks, 'L', _tickLBody, _tickLBodyAt, _tickLGap, _tickLGapAt);
        ticks.Append(" · ");
        AppendTickLocked(ticks, 'C', _tickCBody, _tickCBodyAt, _tickCGap, _tickCGapAt);
        _stallLine += ticks.ToString();
    }

    /// <summary>A342: 루프 하나의 틱 요약 1토막 — 기록이 없으면 "L none"으로만 적는다.</summary>
    private static void AppendTickLocked(StringBuilder text, char loop, long body, int bodyAt, long gap, int gapAt)
    {
        text.Append(loop);
        if (bodyAt < 0)
        {
            text.Append(" none");
            return;
        }
        text.Append(" body ").Append(ToMs(body)).Append("ms@#").Append(bodyAt);
        if (gapAt < 0)
        {
            text.Append(" gap none"); // 틱이 하나뿐이라 간격을 잴 수 없었다
            return;
        }
        text.Append(" gap ").Append(ToMs(gap)).Append("ms@#").Append(gapAt);
    }

    /// <summary>정지가 난 구간의 이름 — 직전 마크와 그다음 마크를 잇는다.
    /// 아직 다음 마크가 오지 않았으면 그 자리를 pending으로 적는다(항해가 그 구간에서 멎었다는 뜻).</summary>
    private static string SegmentNameLocked(int index)
    {
        if (index < 0 || index >= _count) return "start";
        var next = index + 1 < _count ? Names[index + 1] : "pending";
        return Names[index] + ">" + next;
    }

    private static long ToMs(long stamps) => stamps * 1000 / Stopwatch.Frequency;
}
