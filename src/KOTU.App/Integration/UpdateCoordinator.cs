using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using KOTU.Core.Settings;

namespace KOTU.App.Integration;

/// <summary>
/// 앱 전역 업데이트 코디네이터 (A26·A76, v0.105.0) — 프로세스당 1개.
///
/// v0.17.0의 "주기 체크 금지, 설정 진입 시에만" 정책과 v0.27.0의 설정 화면 카운트다운 루프를
/// <b>대체</b>한다. 설정 화면이 닫혀 있어도 백그라운드에서 계속 확인하고, 새 버전을 찾으면
/// 윈도우 네이티브 토스트(Windows App SDK AppNotification, unpackaged 지원)를 띄운다.
/// 설정 화면(SettingsView)은 여기 상태를 <b>표시·조작만</b> 한다.
///
/// 정책(사용자 확정 2026-08-12):
///  · 첫 체크는 시작 30초 뒤 1회, 그 다음부터 10분 간격 (시작 부하·네트워크 회피).
///  · 같은 버전은 토스트 1회만 — 알린 버전을 <c>update.lastNotifiedVersion</c>에 남긴다.
///    다중 인스턴스로 프로세스가 여럿이어도 이 값이 공유 설정이라 중복 토스트가 자연히 억제된다.
///    그 이상의 프로세스 간 조율은 하지 않는다.
///  · 체크 실패(오프라인 등)는 조용히 넘어간다 — 토스트 없음, <c>update.lastCheckError</c>에만 기록.
///  · 토스트 등록 실패(AppNotification을 못 쓰는 환경)도 조용히 무시 — 앱은 그대로 동작한다.
///  · 업데이트 불가 빌드(수동 zip 실행 등)에서는 타이머를 아예 시작하지 않는다.
///
/// 타이머는 UI 스레드 DispatcherTimer라 Tick·상태 변경이 모두 UI 스레드에서 일어난다.
/// 토스트 클릭 콜백만 다른 스레드에서 오므로 저장해 둔 디스패처로 넘긴다.
/// </summary>
internal static class UpdateCoordinator
{
    // ---------- 설정 키 (공유 설정 — 창·인스턴스 공통, A76) ----------

    /// <summary>오토체크 on/off. 기본 true.</summary>
    public const string AutoCheckKey = "update.autoCheck";

    /// <summary>마지막 확인 시각. ISO 8601 UTC("O") 문자열, 없으면 빈 문자열.</summary>
    public const string LastCheckedAtKey = "update.lastCheckedAt";

    /// <summary>마지막 확인의 실패 사유 요약. 빈 문자열 = 성공.</summary>
    public const string LastCheckErrorKey = "update.lastCheckError";

    /// <summary>토스트로 이미 알린 버전. 같은 값이면 다시 띄우지 않는다.</summary>
    public const string LastNotifiedVersionKey = "update.lastNotifiedVersion";

    // ---------- 주기 ----------

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>실패 사유 표기 최대 길이(넘으면 말줄임).</summary>
    private const int ErrorSummaryLimit = 80;

    // ---------- 토스트 인자 ----------

    private const string ToastArgumentKey = "action";
    private const string ToastOpenUpdates = "openUpdates";

    private static ISettingsService? _settings;
    private static WindowManager? _manager;
    private static DispatcherQueue? _dispatcher;
    private static DispatcherTimer? _timer;
    private static bool _initialized;
    private static bool _checking;
    private static bool _notificationsReady;

    // ---------- 전역 상태 (설정 화면이 읽어 표시한다) ----------

    /// <summary>Velopack 관리 하의 빌드인지 — false면 체크·토스트를 하지 않는다.</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>오토체크 on/off (A76). off면 주기 체크·토스트가 멈추고 수동 확인만 남는다.</summary>
    public static bool AutoCheckEnabled { get; private set; } = true;

    /// <summary>마지막 확인 시각(UTC). 한 번도 없으면 null.</summary>
    public static DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>마지막 확인의 실패 사유 요약. 빈 문자열 = 성공.</summary>
    public static string LastCheckError { get; private set; } = string.Empty;

    /// <summary>찾아 둔 새 버전. 오토체크를 꺼도 지우지 않는다(적용 버튼 유지, 사용자 확정).</summary>
    public static Velopack.UpdateInfo? PendingUpdate { get; private set; }

    /// <summary>확인이 진행 중인지.</summary>
    public static bool IsChecking => _checking;

    /// <summary>상태가 바뀌었을 때(UI 스레드). 설정 화면이 구독해 표시를 갱신한다.</summary>
    public static event Action? Changed;

    /// <summary>앱 시작 시 1회(UI 스레드) — 저장된 상태를 읽고 타이머·토스트를 준비한다.</summary>
    public static void Initialize(WindowManager manager)
    {
        if (_initialized) return;
        _initialized = true;

        _manager = manager;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _settings = App.Services.GetRequiredService<ISettingsService>();

        AutoCheckEnabled = _settings.Get(AutoCheckKey, true);
        LastCheckError = _settings.Get(LastCheckErrorKey, string.Empty);
        LastCheckedAt = ParseStamp(_settings.Get(LastCheckedAtKey, string.Empty));

        // 업데이트 불가 빌드면 타이머도 토스트 등록도 하지 않는다.
        // (설정 화면은 토글·시각 표시를 숨기지 않고 비활성으로 남긴다 — 사용자 확정)
        IsAvailable = UpdateService.IsUpdatableBuild;
        if (!IsAvailable) return;

        EnsureNotificationsRegistered();
        if (AutoCheckEnabled) StartTimer();
    }

    /// <summary>오토체크 토글(A76). 저장 후 타이머를 켜거나 끈다.</summary>
    public static void SetAutoCheck(bool enabled)
    {
        if (AutoCheckEnabled == enabled) return;
        AutoCheckEnabled = enabled;
        _settings?.Set(AutoCheckKey, enabled);
        _settings?.Save();

        if (IsAvailable)
        {
            // 다시 켜면 앱 시작과 같은 리듬(30초 뒤 첫 체크 → 10분 간격)으로 되돌린다.
            if (enabled) StartTimer();
            else _timer?.Stop();
        }
        Notify();
    }

    /// <summary>수동 "Check now". 오토체크가 꺼져 있어도 동작한다(토스트는 띄우지 않는다).</summary>
    public static Task CheckNowAsync() => CheckAsync(manual: true);

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

    // ---------- 타이머 ----------

    private static void StartTimer()
    {
        _timer ??= CreateTimer();
        _timer.Interval = FirstCheckDelay; // 시작 30초 뒤 첫 체크
        _timer.Start();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = FirstCheckDelay };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = CheckInterval; // 첫 틱 이후로는 10분 간격
            await CheckAsync(manual: false);
        };
        return timer;
    }

    // ---------- 확인 ----------

    private static async Task CheckAsync(bool manual)
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

        // 토스트는 백그라운드 주기 체크에서만 — 수동 확인은 사용자가 이미 화면을 보고 있다.
        if (info is not null && !manual && AutoCheckEnabled) NotifyNewVersion(info);
        Notify();
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

    // ---------- 네이티브 토스트 (A26) ----------

    /// <summary>
    /// AppNotification 등록 — 실패해도 조용히 넘어간다(토스트만 없고 앱은 정상 동작).
    /// unpackaged 앱에서는 Register()가 COM 활성화자와 시작 메뉴 바로 가기를 만든다.
    /// </summary>
    private static void EnsureNotificationsRegistered()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _notificationsReady = true;
        }
        catch
        {
            _notificationsReady = false;
        }
    }

    private static void NotifyNewVersion(Velopack.UpdateInfo info)
    {
        var version = info.TargetFullRelease.Version.ToString();
        // 같은 버전은 1회만 — 더 새로운 버전이 나오면 값이 달라져 다시 알린다.
        if (_settings?.Get(LastNotifiedVersionKey, string.Empty) == version) return;
        _settings?.Set(LastNotifiedVersionKey, version);
        _settings?.Save();
        ShowToast(version);
    }

    private static void ShowToast(string version)
    {
        if (!_notificationsReady) return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddArgument(ToastArgumentKey, ToastOpenUpdates)
                .AddText($"{Branding.AppName} v{version} is available")
                .AddText($"Click to open the update section in {Branding.AppName} settings.")
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch
        {
            // 알림 표시 실패는 치명적이지 않다 — 조용히 넘어간다.
        }
    }

    /// <summary>토스트 클릭(다른 스레드) → 앱 활성화 + 설정 화면의 업데이트 섹션으로.</summary>
    private static void OnNotificationInvoked(AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue(ToastArgumentKey, out var action)
            || action != ToastOpenUpdates)
        {
            return;
        }
        _dispatcher?.TryEnqueue(() => _manager?.ShowSettings(scrollToUpdates: true));
    }
}
