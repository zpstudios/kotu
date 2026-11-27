using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Core.Updates;

namespace KOTU.App.Integration;

/// <summary>A441: 앱 시작 1회와 마지막 확인 완료 후 24시간. Settings 수명과 독립인 프로세스당 상태.</summary>
internal static class UpdateCoordinator
{
    public const string LastCheckedAtKey = "update.lastCheckedAt";
    public const string LastCheckErrorKey = "update.lastCheckError";
    private static readonly UpdateCheckSchedule Schedule = new();
    private static ISettingsService? _settings;
    private static DispatcherQueue? _dispatcher;
    private static ModuleWorker? _worker;
    private static Timer? _timer;
    private static IDisposable? _owner;
    private static int _cycleQueued;
    private static bool _initialized, _availabilityKnown, _availableOnWorker;
    private static volatile bool _stopped;
    private static long _checkGeneration, _offeredGeneration;
    private static bool _promptPending, _ownsAutomatic;

    internal static UpdateStateStore? Store { get; private set; }
    public static bool IsAvailable { get; private set; }
    public static bool IsChecking { get; private set; }
    public static DateTimeOffset? LastCheckedAt { get; private set; }
    public static DateTimeOffset? NextCheckAt { get; private set; }
    public static string LastCheckError { get; private set; } = string.Empty;
    public static string NotificationError { get; private set; } = string.Empty;
    public static Velopack.UpdateInfo? PendingUpdate { get; private set; }
    public static event Action? Changed;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        LastCheckedAt = DateTimeOffset.TryParse(_settings.Get(LastCheckedAtKey, string.Empty), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var stamp) ? stamp : null;
        LastCheckError = _settings.Get(LastCheckErrorKey, string.Empty);
        if (!KOTU.Core.Integration.DistributionPolicy.IsStableDistribution) return;
        Store = new UpdateStateStore(Path.Combine(Path.GetDirectoryName(_settings.FilePath)!, "updates"));
        _worker = new ModuleWorker("KOTU automatic updates", ThreadPriority.BelowNormal);
        // 짧은 워커 틱은 owner 사망 후 인수와 절전 복귀도 확인한다. 네트워크 주기는 Schedule만 결정한다.
        _timer = new Timer(_ => QueueCycle(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private static async void QueueCycle()
    {
        if (_stopped || _worker is null || Interlocked.Exchange(ref _cycleQueued, 1) != 0) return;
        try { await _worker.Run(_ => RunCycle()).ConfigureAwait(false); }
        catch (Exception ex) { Publish(() => { LastCheckError = Summarize(ex.Message); IsChecking = false; Notify(); }); }
        finally { Interlocked.Exchange(ref _cycleQueued, 0); }
    }

    private static void RunCycle()
    {
        if (_stopped) return;
        if (!_availabilityKnown)
        {
            _availableOnWorker = UpdateService.IsUpdatableBuild;
            _availabilityKnown = true;
            Publish(() => { IsAvailable = _availableOnWorker; Notify(); });
        }
        if (!_availableOnWorker) return;
        var notificationError = string.Empty;
        try { _owner ??= Store!.TryAcquireLease("automatic"); }
        catch (Exception ex) { notificationError = "Update notifications are postponed: " + Summarize(ex.Message); }
        var ownsAutomatic = _owner is not null;
        Publish(() => { _ownsAutomatic = ownsAutomatic; NotificationError = notificationError; });
        // 각 실행은 자체 시작/24시간 확인으로 Settings를 갱신한다. lease는 자동 팝업만 한 프로세스로 제한한다.
        if (!Schedule.TryBegin())
        {
            var next = DateTimeOffset.UtcNow + Schedule.Remaining;
            Publish(() => { NextCheckAt = next; TryOfferUpdate(); });
            return;
        }
        Publish(() => { IsChecking = true; NextCheckAt = null; Notify(); });
        Velopack.UpdateInfo? info = null;
        var error = string.Empty;
        try
        {
            // 전용 BelowNormal 워커에서만 동기 대기한다. 매니저 생성/네트워크/파일 I/O가 UI를 펌프하지 않는다.
            info = UpdateService.CheckAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) { error = Summarize(ex.Message); }
        if (_stopped || !Schedule.Complete()) return;
        var checkedAt = Schedule.CompletedAt!.Value;
        try
        {
            _settings!.Set(LastCheckedAtKey, checkedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            _settings.Set(LastCheckErrorKey, error);
            _settings.Save();
        }
        catch (Exception ex) { error = "Could not save update status: " + Summarize(ex.Message); }
        var resultError = error;
        Publish(() =>
        {
            IsChecking = false;
            LastCheckedAt = checkedAt;
            NextCheckAt = DateTimeOffset.UtcNow + Schedule.Remaining;
            LastCheckError = resultError;
            PendingUpdate = info; // 실패/철회된 피드의 낡은 자동 안내를 유지하지 않는다.
            _checkGeneration++;
            _promptPending = resultError.Length == 0 && info is not null;
            Notify();
            TryOfferUpdate();
        });
    }

    /// <summary>UI 스레드. 모달/Welcome/숨김 창은 다음 활성화나 워커 틱까지 보류하며 강제로 활성화하지 않는다.</summary>
    internal static void TryOfferUpdate()
    {
        if (_stopped || !_ownsAutomatic || !_promptPending || _offeredGeneration == _checkGeneration ||
            App.IsWelcomePending || UpdateFlow.IsBusy || PendingUpdate is not { } info || Store is null) return;
        var installed = UpdateVersion.FromInstalled(typeof(App).Assembly.GetName().Version!);
        if (!UpdateVersion.TryParse(info.TargetFullRelease.Version.ToString(), out var target) || !target.ShouldPromptOver(installed))
        { _promptPending = false; return; }
        var manager = App.Services.GetRequiredService<WindowManager>();
        if (manager.FindUpdateHost() is not { } window) return;
        // 표시를 시작한 확인 세대는 Escape로 닫아도 즉시 반복하지 않는다. No만 영구 저장한다.
        _offeredGeneration = _checkGeneration;
        _ = OfferAsync(window, info, target);
    }

    private static async Task OfferAsync(MainWindow window, Velopack.UpdateInfo info, UpdateVersion target)
    {
        try
        {
            var shown = await UpdateFlow.OfferAsync(window, info, target, Store!);
            if (!shown && !_stopped) _offeredGeneration = 0;
        }
        catch (Exception ex) { LastCheckError = Summarize(ex.Message); Notify(); }
    }

    public static void Shutdown()
    {
        if (_stopped) return;
        _stopped = true;
        _timer?.Dispose();
        Schedule.Dispose();
        // 이미 진행 중인 확인 뒤에 lease를 놓아 다른 프로세스와 확인/안내가 겹치지 않게 한다.
        _worker?.Post(() => { _owner?.Dispose(); _owner = null; });
        _worker?.Dispose();
    }

    public static string DescribeLastCheck()
    {
        if (LastCheckedAt is not { } at) return "Last checked: never";
        var local = at.ToLocalTime();
        return LastCheckError.Length == 0 ? $"Last checked: {local:yyyy-MM-dd HH:mm}"
            : $"Last checked: {local:HH:mm} - failed ({LastCheckError})";
    }

    private static string Summarize(string? message)
    {
        var line = (message ?? string.Empty).Replace('\r', '\n').Split('\n')[0].Trim();
        return line.Length > 80 ? line[..77] + "..." : line;
    }
    private static void Publish(Action action) => _dispatcher?.TryEnqueue(() => { if (!_stopped) action(); });
    private static void Notify() => Changed?.Invoke();
}
