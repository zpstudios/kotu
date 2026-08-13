using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using KOTU.Core.Settings;

namespace KOTU.App.Integration;

/// <summary>
/// 앱 전역 업데이트 코디네이터 — 프로세스당 1개.
///
/// 확인은 <b>설정 화면의 "Check now"를 눌렀을 때만</b> 돈다(A95, v0.117.0). 타이머도 토스트도 없다.
/// 여기 남는 건 여러 창이 함께 봐야 하는 전역 상태 1벌 — 확인 중인지, 마지막 확인 시각·실패 사유,
/// 찾아 둔 새 버전 — 이고 설정 화면(SettingsView)은 그 상태를 <b>표시만</b> 한다.
/// 창 A에서 확인하면 창 B의 설정 화면도 <see cref="Changed"/>로 따라 갱신된다.
///
/// 정책 이력 — 같은 주제로 <b>세 번</b> 뒤집혔다. 또 뒤집기 전에 읽을 것:
///  · v0.17.0  주기 체크 금지, 설정 진입 시에만.
///  · v0.27.0  설정 화면 체류 중 1분 카운트다운 루프.
///  · v0.105.0 (A26·A76) 시작 30초 뒤 첫 체크 + 10분 간격 타이머 + 네이티브 토스트 + 오토체크 토글.
///  · v0.117.0 (A95) <b>현행</b> — 타이머·토스트·오토체크 토글을 전부 걷어내고 수동 Check now만 남겼다.
///    마지막 확인 시각 표시(A76)는 유지.
///
/// 확인 실패(오프라인 등)는 예외를 밖으로 던지지 않고 <c>update.lastCheckError</c>에 요약만 남긴다 —
/// 설정 화면이 그 문구를 그대로 보여 준다.
/// 업데이트 불가 빌드(수동 zip 실행 등)에서는 확인 자체를 하지 않는다(화면은 숨기지 않고 비활성).
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

    private static ISettingsService? _settings;
    private static DispatcherQueue? _dispatcher;
    private static bool _initialized;
    private static bool _checking;

    // ---------- 전역 상태 (설정 화면이 읽어 표시한다) ----------

    /// <summary>Velopack 관리 하의 빌드인지 — false면 확인도 적용도 할 수 없다.</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>마지막 확인 시각(UTC). 한 번도 없으면 null.</summary>
    public static DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>마지막 확인의 실패 사유 요약. 빈 문자열 = 성공.</summary>
    public static string LastCheckError { get; private set; } = string.Empty;

    /// <summary>찾아 둔 새 버전. 뒤이은 확인이 실패해도 지우지 않는다(적용 버튼 유지).</summary>
    public static Velopack.UpdateInfo? PendingUpdate { get; private set; }

    /// <summary>확인이 진행 중인지.</summary>
    public static bool IsChecking => _checking;

    /// <summary>상태가 바뀌었을 때(UI 스레드). 설정 화면이 구독해 표시를 갱신한다.</summary>
    public static event Action? Changed;

    /// <summary>
    /// 앱 시작 시 1회(UI 스레드) — 저장해 둔 마지막 확인 결과를 읽어 놓기만 한다.
    /// 시작 시점에는 <b>확인하지 않는다</b>(A95).
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
    /// 수동 "Check now" (A95) — 업데이트를 확인하는 <b>유일한</b> 경로다.
    /// 예외는 밖으로 나가지 않는다(사유는 <see cref="LastCheckError"/>로 흘린다).
    /// </summary>
    public static async Task CheckNowAsync()
    {
        if (!IsAvailable || _checking) return;
        _checking = true;
        Notify();

        var error = string.Empty;
        Velopack.UpdateInfo? info = null;
        try
        {
            info = await UpdateService.CheckAsync();
        }
        catch (Exception ex)
        {
            error = Summarize(ex.Message);
        }

        var checkedAt = DateTimeOffset.UtcNow;
        LastCheckedAt = checkedAt;
        LastCheckError = error;
        // 실패했다고 이미 찾아 둔 정보를 지우지는 않는다(적용 버튼 유지).
        if (info is not null) PendingUpdate = info;

        // 저장은 UTC ISO 8601("O") — 표시할 때만 로컬로 바꾼다(A76).
        _settings?.Set(LastCheckedAtKey,
            checkedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        _settings?.Set(LastCheckErrorKey, error);
        _settings?.Save();

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
            : $"Last checked: {local:HH:mm} — failed ({LastCheckError})";
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
