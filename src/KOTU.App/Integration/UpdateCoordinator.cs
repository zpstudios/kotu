using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using KOTU.Core.Settings;

namespace KOTU.App.Integration;

/// <summary>
/// 앱 전역 업데이트 코디네이터 — 프로세스당 1개.
///
/// 확인 경로는 둘(A114, v0.136.0 — 셋째였던 수동 "Check now" 버튼은 A125/v0.148.0에서 제거.
/// ①의 조건은 A206, v0.215.0에서 "상시"에서 "설정 화면 체류 중"으로 좁혀졌다):
/// <b>① 설정 화면이 열려 있는 동안 2분 간격 자동 확인</b>(프로세스당 타이머 1개 —
/// 창을 몇 개 열든 하나다. <see cref="NotifySettingsOpened"/>/<see cref="NotifySettingsClosed"/>의
/// 열림 참조 카운트가 0→1일 때 켜지고 1→0일 때 꺼진다) ·
/// <b>② 설정 화면 진입 시 즉시 1회</b>(SettingsView가 부른다).
/// 둘 다 <see cref="CheckNowAsync"/> 하나로 모이고, 확인이 진행 중이면
/// 조용히 반환하므로 서로 겹쳐도 요청이 두 번 나가지 않는다.
/// (메서드 이름 <c>CheckNowAsync</c>는 수동 버튼에서 온 이름이지만 그대로 둔다 — 이름을 바꿔도
/// 얻는 게 없고 A114 이래 문서·주석이 전부 이 이름으로 ①②를 가리킨다.)
/// 새 버전을 찾아도 <b>팝업·토스트는 절대 띄우지 않는다</b> — 알림 방식 = (b) 조용히 반영만
/// (2026-08-14 사용자 확정): 설정 화면 업데이트 섹션의 최신 버전 줄·[Update to vX] 버튼에만 나타난다.
/// 여기 남는 건 여러 창이 함께 봐야 하는 전역 상태 1벌 — 확인 중인지, 마지막 확인 시각·실패 사유,
/// 찾아 둔 새 버전 — 이고 설정 화면(SettingsView)은 그 상태를 <b>표시만</b> 한다.
/// 창 A에서 확인하면 창 B의 설정 화면도 <see cref="Changed"/>로 따라 갱신된다.
///
/// 정책 이력 — 같은 주제로 <b>여섯 번</b> 뒤집혔다. 또 뒤집기 전에 읽을 것:
///  · v0.17.0  주기 체크 금지, 설정 진입 시에만.
///  · v0.27.0  설정 화면 체류 중 1분 카운트다운 루프.
///  · v0.105.0 (A26·A76) 시작 30초 뒤 첫 체크 + 10분 간격 타이머 + 네이티브 토스트 + 오토체크 토글.
///  · v0.117.0 (A95) 타이머·토스트·오토체크 토글을 전부 걷어내고 수동 Check now만 남겼다.
///  · v0.136.0 (A114) A26의 타이머 구조만 되살렸다(간격 2분·첫 확인도 2분 뒤).
///    <b>토스트는 되살리지 않았다</b>(AppNotification 등록·발행·클릭 경로 전부 부활 금지) —
///    오토체크 토글(<c>update.autoCheck</c>)·<c>update.lastNotifiedVersion</c>도 그대로 폐기 상태다.
///  · v0.148.0 (A125) 수동 "Check now" 버튼을 뺐다. A114의 ①② 자동 경로는 그대로라
///    확인 빈도는 바뀌지 않았다. 사람이 확인을 시키는 손잡이만 사라진 것이다(설정 화면을 열면
///    ②가 곧 그 역할을 한다). 토스트·오토체크 토글은 여전히 없다.
///  · v0.215.0 (A206) <b>현행</b> — ①의 "상시"를 폐지했다. 타이머는 <b>설정 화면이 열려 있는
///    동안에만</b> 돌고(열림 참조 카운트 0→1 시작 · 1→0 정지), 설정을 보지 않는 동안에는
///    확인이 아예 없다. 간격 2분·②의 진입 1회·토스트 없음·오토체크 토글 없음은 그대로다.
///    근거: 확인 결과를 보여 주는 화면이 설정뿐이라 그 밖에서 도는 확인은 아무도 보지 않는다.
///
/// 스레드 지도(A278 — 주기 작업을 UI 스레드에서 돌리지 않는다는 사용자 정책):
///  · <b>타이머 틱</b> = UI 스레드. 하는 일은 <see cref="NextCheckAt"/> 갱신과 확인 착수 두 줄뿐이다.
///  · <b>네트워크 요청·응답 파싱·설정 파일 쓰기</b> = 워커 스레드(<c>Task.Run</c> →
///    <see cref="RunCheckAsync"/>). Velopack의 매니저 생성·<c>IsInstalled</c> 판별까지 전부 여기 안이라
///    UI 스레드에는 동기 I/O가 한 줄도 남지 않는다.
///  · <b>결과 반영</b>(전역 상태 + <see cref="Changed"/>) = 다시 UI 스레드. 워커가
///    <c>DispatcherQueue.TryEnqueue</c>로 되돌린다. 큐가 이미 죽었으면(프로세스 종료 중)
///    반환값을 무시하고 조용히 흘린다.
/// 진행 중 재진입은 <see cref="CheckNowAsync"/>의 <c>_checking</c> 가드가 막고(조용히 스킵),
/// 화면이 다 닫힌 뒤 도착한 응답은 <see cref="_checkCts"/>의 취소 토큰이 걸러 낸다.
///
/// 확인 실패(오프라인 등)는 예외를 밖으로 던지지 않고 <c>update.lastCheckError</c>에 요약만 남긴다 —
/// 설정 화면이 그 문구를 그대로 보여 준다.
/// 업데이트 불가 빌드(수동 zip 실행 등)에서는 확인 자체를 하지 않는다(설정 화면을 열어도
/// 타이머를 만들지 않고 <see cref="CheckNowAsync"/>도 즉시 되돌아간다. 화면은 숨기지 않고 비활성).
/// </summary>
internal static class UpdateCoordinator
{
    // ---------- 설정 키 (공유 설정 — 창·인스턴스 공통, A76) ----------
    // ※ 구 키 update.autoCheck·update.lastNotifiedVersion은 A95(v0.117.0)에서 읽지 않는다.
    //    기존 settings.json에 남아 있어도 무해한 잔여값이라 지우는 마이그레이션은 넣지 않았다.

    /// <summary>마지막 확인 시각. ISO 8601 UTC("O") 문자열, 없으면 빈 문자열.</summary>
    public const string LastCheckedAtKey = "update.lastCheckedAt";

    /// <summary>마지막 확인의 실패 사유 요약. 빈 문자열 = 성공.</summary>
    public const string LastCheckErrorKey = "update.lastCheckError";

    /// <summary>실패 사유 표기 최대 길이(넘으면 말줄임).</summary>
    private const int ErrorSummaryLimit = 80;

    /// <summary>
    /// 자동 확인 간격(A114 — 2분 유지). A206(v0.215.0)에서 기준점이 <b>앱 시작</b>에서
    /// <b>설정 화면을 연 시점</b>으로 옮겨졌다: 진입 즉시 1회(경로 ②)가 돌고, 타이머의 첫 틱은
    /// 그로부터 이 간격 뒤다. A114의 "시작 직후 네트워크 러시 회피"는 전제가 사라졌다 —
    /// 앱 시작만으로는 이제 타이머가 서지 않는다.
    /// DispatcherTimer는 반복 타이머라 Interval 하나로 첫 틱·이후 주기가 모두 정해진다.
    /// </summary>
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromMinutes(2);

    private static ISettingsService? _settings;
    private static DispatcherQueue? _dispatcher;

    /// <summary>
    /// 자동 확인 타이머(A114) — <b>프로세스당 1개</b>. 창을 몇 개 열든 늘지 않는다
    /// (정적 필드 + <see cref="StartAutoCheckTimer"/>의 null 가드). UI 스레드 DispatcherTimer라
    /// Tick·상태 변경이 모두 UI 스레드에서 일어난다(A26 전례와 동일 구조).
    /// A206: 이제 <b>설정 화면이 하나라도 열려 있는 동안에만</b> 존재한다 — 없으면(null) 자동
    /// 확인도 없다는 뜻이다. 정지할 때 필드를 null로 되돌리므로 위 가드가 재시작도 함께 지킨다.
    /// </summary>
    private static DispatcherTimer? _timer;

    /// <summary>
    /// 지금 열려 있는 설정 화면 수(A206). 설정 화면은 창마다 뜰 수 있는데 이 클래스는 static이라,
    /// 단순 열림/닫힘 배선이면 두 창 중 한쪽만 닫아도 타이머가 죽는다 — 그래서 카운트다.
    /// 0→1에서 타이머를 켜고 1→0에서 끈다. UI 스레드에서만 오간다(SettingsView의 Loaded/Unloaded).
    /// </summary>
    private static int _settingsOpenCount;

    private static bool _initialized;

    /// <summary>
    /// 확인이 진행 중인지 — 재진입 가드(A114)이자 설정 화면의 "Checking..." 표시원이다.
    /// A278에서 실제 확인이 워커로 나갔지만 이 플래그는 <b>UI 스레드에서만</b> 오간다:
    /// true는 <see cref="CheckNowAsync"/>(UI), false는 워커가 디스패처로 되돌린
    /// <see cref="ApplyCheckResult"/>(UI) 또는 <see cref="CancelPendingCheck"/>(UI)에서만 찍힌다.
    /// 워커 스레드는 이 필드를 읽지도 쓰지도 않으므로 메모리 가시성 문제가 생기지 않는다.
    /// </summary>
    private static bool _checking;

    /// <summary>
    /// 진행 중인 확인의 취소 신호(A278). 마지막 설정 화면이 닫힐 때
    /// <see cref="CancelPendingCheck"/>가 켜고, 워커는 응답을 받은 직후와 UI 큐에서 깨어난 직후
    /// 두 번 이 토큰을 본다 — 켜져 있으면 <b>부분 결과를 통째로 버린다</b>(상태도 설정 파일도
    /// 건드리지 않는다). 창이 닫히는 중에 응답이 도착해도 안전한 근거가 이것이다.
    /// Velopack의 확인 API에는 넘기지 않는다(취소 인자를 받는 오버로드에 선례가 없어 새 API를
    /// 끌어들이지 않았다) — 요청 자체는 끝까지 가고 결과만 폐기된다.
    /// UI 스레드에서만 만들고 취소한다. Dispose는 하지 않는다: 등록·타이머가 붙지 않은
    /// CancellationTokenSource라 회수는 GC에 맡기는 편이 안전하고(워커가 뒤늦게 토큰을 읽는다),
    /// 저장소 선례(ContentInfoOverlay의 _selectionCts)도 같은 방식이다.
    /// </summary>
    private static CancellationTokenSource? _checkCts;

    // ---------- 전역 상태 (설정 화면이 읽어 표시한다) ----------

    /// <summary>Velopack 관리 하의 빌드인지 — false면 확인도 적용도 할 수 없다.</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>마지막 확인 시각(UTC). 한 번도 없으면 null.</summary>
    public static DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>
    /// 다음 <b>자동</b> 확인 예정 시각(UTC) — A167(v0.171.0). 타이머가 없으면 null이고
    /// (업데이트 불가 빌드 · A206: 설정 화면이 다 닫힌 뒤), 설정 화면이 남은 시간을
    /// 카운트다운으로 보여 준다. null이면 그 줄은 접힌다 — "예정 없음"을 문구로 말하지 않는다.
    ///
    /// 값을 찍는 곳은 <b>타이머를 여닫는 세 지점뿐</b>이다: <see cref="StartAutoCheckTimer"/>의 Start
    /// 직후와 매 Tick, 그리고 <see cref="StopAutoCheckTimer"/>(null로 되돌린다).
    /// <see cref="CheckNowAsync"/>에서는 건드리지 않는다 —
    /// 설정 화면 진입 1회 확인(경로 ②)은 타이머를 재시작하지 않으므로 다음 틱 시각도 밀리지 않기 때문이다.
    /// 같은 이유로 <c>LastCheckedAt + 2분</c>으로 계산하면 틀린다(그 계산은 ②가 돌 때마다 어긋난다).
    ///
    /// <see cref="LastCheckedAt"/>과 같은 규약: UI 스레드에서만 쓰고 UTC로 담는다.
    /// </summary>
    public static DateTimeOffset? NextCheckAt { get; private set; }

    /// <summary>마지막 확인의 실패 사유 요약. 빈 문자열 = 성공.</summary>
    public static string LastCheckError { get; private set; } = string.Empty;

    /// <summary>찾아 둔 새 버전. 뒤이은 확인이 실패해도 지우지 않는다(적용 버튼 유지).</summary>
    public static Velopack.UpdateInfo? PendingUpdate { get; private set; }

    /// <summary>확인이 진행 중인지.</summary>
    public static bool IsChecking => _checking;

    /// <summary>상태가 바뀌었을 때(UI 스레드). 설정 화면이 구독해 표시를 갱신한다.</summary>
    public static event Action? Changed;

    /// <summary>
    /// 앱 시작 시 1회(UI 스레드) — 저장해 둔 마지막 확인 결과를 읽어 상태를 채우기만 한다.
    /// A206(v0.215.0): 여기서 <b>타이머를 켜지 않는다</b>. 앱을 켜 두기만 해서는 확인이 한 번도
    /// 돌지 않고, 첫 확인은 사용자가 설정 화면에 들어가는 순간이다(경로 ②) —
    /// 그때 <see cref="NotifySettingsOpened"/>가 주기 타이머까지 함께 세운다.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _settings = App.Services.GetRequiredService<ISettingsService>();

        LastCheckError = _settings.Get(LastCheckErrorKey, string.Empty);
        LastCheckedAt = ParseStamp(_settings.Get(LastCheckedAtKey, string.Empty));

        // 업데이트 불가 빌드에서도 설정 화면은 표시를 숨기지 않고 비활성으로 남긴다(사용자 확정).
        IsAvailable = UpdateService.IsUpdatableBuild;
    }

    /// <summary>
    /// 설정 화면이 하나 열렸다(A206) — SettingsView의 Loaded가 부른다. 열림 수가 0→1이면
    /// 자동 확인 타이머를 세운다. 이미 다른 창의 설정이 열려 있으면 카운트만 오르고 타이머는
    /// 그대로 둔다(재시작 금지 — 돌던 주기가 창을 열 때마다 밀리면 안 된다).
    /// 업데이트 불가 빌드에서는 카운트만 세고 타이머는 만들지 않는다(종전과 같은 무동작).
    /// 진입 즉시 1회 확인(경로 ②)은 여기가 아니라 부르는 쪽이 <see cref="CheckNowAsync"/>로 한다 —
    /// 이 메서드는 "주기"만 책임진다.
    /// </summary>
    public static void NotifySettingsOpened()
    {
        _settingsOpenCount++;
        if (_settingsOpenCount == 1 && IsAvailable) StartAutoCheckTimer();
    }

    /// <summary>
    /// 설정 화면이 하나 닫혔다(A206) — SettingsView의 Unloaded가 부른다. 마지막 하나가 닫힐 때
    /// (1→0)만 타이머를 멈춘다. 짝이 맞지 않는 닫힘(이미 0)은 조용히 무시한다 — 카운트를
    /// 음수로 만들면 그 뒤의 열림이 타이머를 못 세운다.
    /// A278: 같은 1→0 지점에서 <b>진행 중인 확인도 취소</b>한다(타이머 정지보다 먼저 —
    /// 상태를 다 바꾼 뒤에 <see cref="StopAutoCheckTimer"/>의 Notify 한 번으로 알리기 위해).
    /// 설정 화면을 띄운 채 창을 닫는 경로도 여기로 온다(MainWindow의 Closed →
    /// SettingsView.ReleaseUpdateWatch → 이 메서드)이므로, 창 닫힘과 응답 도착이 겹치는
    /// 경합이 이 한 지점에서 함께 막힌다.
    /// </summary>
    public static void NotifySettingsClosed()
    {
        if (_settingsOpenCount == 0) return;
        _settingsOpenCount--;
        if (_settingsOpenCount != 0) return;
        CancelPendingCheck();
        StopAutoCheckTimer();
    }

    /// <summary>
    /// 진행 중인 확인을 폐기한다(A278) — 취소 토큰을 켜고 재진입 가드를 즉시 풀어 준다.
    /// 워커는 계속 돌지만 돌아온 결과가 토큰에 걸려 버려지므로, 다음에 설정 화면을 열었을 때
    /// 낡은 응답이 뒤늦게 화면을 덮어쓰는 일이 없다.
    /// 가드를 여기서 푸는 이유: 취소된 확인의 UI 반영 경로가 통째로 건너뛰어지므로
    /// 누군가는 <c>_checking</c>을 되돌려야 하고, 그 자리가 UI 스레드인 여기다
    /// (안 풀면 그 뒤의 확인이 영영 "진행 중"으로 막힌다).
    /// Notify는 하지 않는다 — 부르는 쪽(<see cref="NotifySettingsClosed"/>)이 곧바로
    /// <see cref="StopAutoCheckTimer"/>로 한 번에 알린다.
    /// </summary>
    private static void CancelPendingCheck()
    {
        if (_checkCts is not { } cts) return;
        _checkCts = null;
        cts.Cancel();
        _checking = false;
    }

    /// <summary>
    /// 자동 확인 타이머 시작(A114, 조건은 A206) — 2분마다 <see cref="CheckNowAsync"/>를 부른다.
    /// 멈추는 경로는 <see cref="StopAutoCheckTimer"/> 하나뿐이고, 부르는 곳도
    /// <see cref="NotifySettingsOpened"/> 하나뿐이다 — 설정 화면 밖에서는 타이머가 서지 않는다.
    /// Tick은 UI 스레드에서 오고, 확인 중이면 CheckNowAsync가 스스로 되돌아간다(중복 요청 없음).
    /// A278: Tick 자체는 UI 디스패처 타이머 그대로 두되 <b>틱 안에서 하는 일은 두 줄로 줄였다</b> —
    /// 예정 시각 갱신과 확인 착수뿐이고, 실제 네트워크·파싱은 CheckNowAsync가 워커로 넘긴다.
    /// </summary>
    private static void StartAutoCheckTimer()
    {
        if (_timer is not null) return; // 프로세스당 1개 — 재진입해도 새로 만들지 않는다
        _timer = new DispatcherTimer { Interval = AutoCheckInterval };
        _timer.Tick += (_, _) =>
        {
            // A167: 다음 예정 시각은 Tick 시점에 다시 찍는다. DispatcherTimer는 반복 타이머라
            // 확인이 얼마나 걸리든 Interval마다 오므로, 확인이 끝난 뒤가 아니라 여기서 재는 게 맞다.
            NextCheckAt = DateTimeOffset.UtcNow + AutoCheckInterval;
            // A278: 발사 후 망각 — 틱 핸들러를 async void로 만들지 않는다. 결과는 워커가
            // 디스패처로 되돌리고, 예외는 CheckNowAsync 안에서 전부 삼켜진다.
            _ = CheckNowAsync();
        };
        _timer.Start();
        NextCheckAt = DateTimeOffset.UtcNow + AutoCheckInterval; // 첫 틱도 Interval 뒤다(A114)
    }

    /// <summary>
    /// 자동 확인 타이머 정지(A206) — 마지막 설정 화면이 닫힐 때만 온다.
    /// 멈춘 뒤 필드를 null로 되돌린다: 다음에 설정을 열면 <see cref="StartAutoCheckTimer"/>가
    /// 새 타이머를 만들어 그 시점을 기준으로 다시 2분을 센다. Stop만 하고 인스턴스를 재사용하면
    /// Start 쪽의 <c>_timer is not null</c> 가드에 걸려 재시작이 조용히 무시되므로,
    /// 가드를 그대로 두는 대신 여기서 null로 만드는 쪽이 코드가 단순하다.
    /// <see cref="NextCheckAt"/>도 함께 비운다 — 예정이 없는데 카운트다운이 남으면 안 된다
    /// (설정 화면의 카운트다운 줄은 null이면 접힌다).
    /// </summary>
    private static void StopAutoCheckTimer()
    {
        if (_timer is not { } timer) return;
        timer.Stop();
        _timer = null;
        NextCheckAt = null;
        Notify(); // 지금 이 순간 표시 중인 화면은 없지만, 전역 상태가 바뀌었으니 알리는 게 규약이다
    }

    /// <summary>
    /// 업데이트 확인의 <b>단일 종착점</b>(A114): 설정 화면 진입 1회 ·
    /// 설정 체류 중 2분 주기 타이머가 전부 여기로 모인다(수동 버튼 경로는 A125/v0.148.0에서 사라졌고,
    /// 주기 타이머의 "상시"는 A206/v0.215.0에서 "설정 체류 중"으로 좁혀졌다).
    /// 진행 중이면 곧바로 반환한다 —
    /// 설정 진입과 타이머 틱이 겹쳐도 네트워크 요청은 하나뿐이라는 근거가 이 한 줄이다.
    /// 예외는 밖으로 나가지 않는다(사유는 <see cref="LastCheckError"/>로 흘린다).
    /// 새 버전을 찾아도 여기서는 <b>상태만 갱신</b>한다 — 토스트·팝업은 없다(A114 알림 방식 b).
    ///
    /// A278: 이 메서드가 UI 스레드에서 하는 일은 <b>가드 판정 · 플래그 세우기 · 취소원 준비 ·
    /// 워커 착수</b>가 전부다(전부 동기·비I/O). 반환 Task는 <b>워커 쪽 일의 완료</b>를 뜻하고
    /// UI 반영까지 기다리지 않는다 — 부르는 두 곳 모두 결과를 await로 받지 않고
    /// <see cref="Changed"/>로 받기 때문이다. <c>async</c>를 붙이지 않은 것도 같은 이유다:
    /// UI 스레드에서 재개할 지점이 없어야 창이 닫히는 중에도 매달리는 연속이 남지 않는다.
    /// </summary>
    public static Task CheckNowAsync()
    {
        if (!IsAvailable || _checking) return Task.CompletedTask; // 진행 중이면 조용히 스킵
        _checking = true;

        // 직전 확인의 취소원이 남아 있을 일은 없다(가드가 하나만 살아 있게 한다). 매 확인마다
        // 새로 만들어 두면 취소 지점은 "지금 도는 그 확인"만 정확히 겨눈다.
        // Notify보다 먼저 세운다 — Changed 구독자가 UI 스레드에서 동기로 불리므로, 그 안에서
        // 무슨 일이 벌어져도 "진행 중 = 취소원 있음"이 이미 짝을 이루고 있어야 한다.
        var cts = _checkCts = new CancellationTokenSource();
        Notify();

        return Task.Run(() => RunCheckAsync(cts.Token));
    }

    /// <summary>
    /// 확인 본체(A278) — <b>워커 스레드에서만 돈다</b>. Velopack 매니저 생성·설치 여부 판별·
    /// HTTP 요청·피드 파싱이 전부 이 안이고, 결과 저장(설정 파일 쓰기)까지 여기서 끝낸다
    /// (<c>JsonSettingsService</c>가 내부 lock으로 보호돼 워커 쓰기가 안전하다).
    /// UI로 돌려보내는 것은 메모리 상태 반영 한 조각뿐이다.
    ///
    /// 예외는 밖으로 내보내지 않는다 — 이 Task를 await하는 곳이 없으므로(틱은 발사 후 망각)
    /// 새어 나가면 관측되지 않은 예외가 된다. 실패 사유는 <see cref="LastCheckError"/>로만 흐른다.
    /// </summary>
    private static async Task RunCheckAsync(CancellationToken cancellation)
    {
        var error = string.Empty;
        Velopack.UpdateInfo? info = null;
        try
        {
            // Task.Run 안이라 SynchronizationContext가 없다 — 내부 await의 재개도 전부 워커/풀이다.
            info = await UpdateService.CheckAsync();
        }
        catch (Exception ex)
        {
            error = Summarize(ex.Message);
        }

        var checkedAt = DateTimeOffset.UtcNow;

        // 취소됐다면 여기서 끝 — 부분 결과는 버린다(설정 파일도 건드리지 않는다).
        if (cancellation.IsCancellationRequested) return;

        // 저장은 UTC ISO 8601("O") — 표시할 때만 로컬로 바꾼다(A76).
        _settings?.Set(LastCheckedAtKey,
            checkedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        _settings?.Set(LastCheckErrorKey, error);
        _settings?.Save();

        // UI 반영만 디스패처로. 반환값은 일부러 보지 않는다 — false면 큐가 이미 죽었다는 뜻이고
        // (프로세스 종료 중) 그때 할 일은 조용히 흘려보내는 것뿐이다.
        if (_dispatcher is { } dispatcher)
            dispatcher.TryEnqueue(() => ApplyCheckResult(cancellation, checkedAt, error, info));
    }

    /// <summary>
    /// 워커가 가져온 결과를 전역 상태에 반영한다(A278) — <b>UI 스레드에서만</b> 불린다.
    /// 큐에 들어간 뒤 화면이 닫혔을 수 있으므로 토큰을 한 번 더 본다.
    /// </summary>
    private static void ApplyCheckResult(CancellationToken cancellation, DateTimeOffset checkedAt,
        string error, Velopack.UpdateInfo? info)
    {
        if (cancellation.IsCancellationRequested) return; // 큐에서 대기하는 사이 폐기됨

        LastCheckedAt = checkedAt;
        LastCheckError = error;
        // 실패했다고 이미 찾아 둔 정보를 지우지는 않는다(적용 버튼 유지).
        if (info is not null) PendingUpdate = info;

        _checkCts = null;
        _checking = false;
        Notify();
    }

    /// <summary>
    /// "Last checked: ..." 한 줄 (A76). 저장은 UTC지만 표시는 로컬 시각으로 바꾼다.
    /// 성공 = "Last checked: 2026-08-12 14:32", 실패 = "Last checked: 14:32 — failed (사유)",
    /// 한 번도 없으면 "Last checked: never".
    /// </summary>
    public static string DescribeLastCheck()
    {
        if (LastCheckedAt is not { } at) return "Last checked: never";
        var local = at.ToLocalTime();
        return LastCheckError.Length == 0
            ? $"Last checked: {local:yyyy-MM-dd HH:mm}"
            : $"Last checked: {local:HH:mm} - failed ({LastCheckError})";
    }

    /// <summary>예외 메시지 첫 줄만, 길면 말줄임.</summary>
    private static string Summarize(string? message)
    {
        var line = (message ?? string.Empty).Replace('\r', '\n').Split('\n')[0].Trim();
        return line.Length > ErrorSummaryLimit ? line[..(ErrorSummaryLimit - 3)] + "..." : line;
    }

    private static void Notify()
    {
        if (Changed is not { } handler) return;
        if (_dispatcher is { HasThreadAccess: false } dispatcher) dispatcher.TryEnqueue(() => handler());
        else handler();
    }

    private static DateTimeOffset? ParseStamp(string stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return null;
        return DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }
}
