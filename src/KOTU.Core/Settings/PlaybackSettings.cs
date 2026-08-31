namespace KOTU.Core.Settings;

/// <summary>
/// A258(v0.258.0): 설정 화면의 <b>공용 Playback 절</b>에 서는 재생 옵션 키 모음. 다른 재생
/// 설정은 모듈 접두어(video.* / audio.*)로 갈라 두지만 이 절의 값들은 모듈별로 갈리지 않고
/// (AutoNextKey는 두 플레이어가 한 값을 공유하고, KeepDisplayAwakeKey는 영상만 쓰되 상수를
/// 설정 화면과 모듈이 함께 참조한다) 어느 모듈에도 속하지 않는 Core에 둔다 —
/// 두 모듈과 설정 화면이 모두 KOTU.Core를 참조하므로 상수 한 벌로 충분하다.
/// 쓰는 곳 = 설정 화면 토글(즉시 Set+Save), 읽는 곳 = 플레이어들이다.
/// A306(2026-08-31): 두 번째 키(<see cref="KeepDisplayAwakeKey"/>)가 들어오면서 이 절의
/// "값은 라이브로만 읽는다" 규칙이 키마다 갈렸다:
///   <see cref="AutoNextKey"/>        = 파일이 끝날 때마다 한 번 읽으면 되므로 변경 이벤트 없음(A258 그대로).
///   <see cref="KeepDisplayAwakeKey"/> = 재생 <b>도중</b>에 끄면 그 자리에서 억제가 풀려야 해서
///                                       변경 알림이 필요하다 — UiScale·ShellDiagnostics의
///                                       "SettingKey + Changed + NotifyChanged" 관용구를 그대로
///                                       옮겨 왔다(구독자가 셸이 아니라 모듈이라 Core에 둔다).
/// 두 키 다 캐시는 두지 않는다(읽기는 항상 ISettingsService 경유).
/// </summary>
public static class PlaybackSettings
{
    /// <summary>
    /// 현재 파일이 끝나면 같은 폴더의 다음 파일을 이어서 재생할지(bool). 루프 모드가
    /// <b>'없음'일 때만</b> 효력이 있다 — 목록 루프·한 파일 루프가 켜져 있으면 그 모드가 이긴다
    /// (A258 확정). 판정 지점은 양 플레이어 AdvanceAfterEnd의 전이 2 진입부 한 곳이다.
    /// </summary>
    public const string AutoNextKey = "player.autoNext";

    /// <summary>기본값 = true(A255까지의 동작 그대로). Get 호출 세 곳이 같은 값을 쓰도록 여기 둔다.</summary>
    public const bool AutoNextDefault = true;

    /// <summary>
    /// A306(v0.290.0): 영상이 <b>실제로 재생 중</b>인 동안 화면보호기·디스플레이 꺼짐을 억제할지(bool).
    /// 억제 자체는 <see cref="KOTU.Core.Integration.DisplayAwakeHook"/>(셸의 kernel32
    /// SetThreadExecutionState 구현이 배선된다)가 하고, 이 키는 그 훅을 부를지 말지만 정한다.
    /// 소비처는 <b>영상 모듈뿐</b>이다 — 오디오는 화면을 볼 일이 없어 범위 밖(A306 확정).
    /// 그럼에도 audio.*·video.* 가 아닌 player.* 접두어로 두는 이유: 설정 화면의 공용 Playback
    /// 절에 서는 토글이고, 상수를 설정 화면(KOTU.App)과 영상 모듈이 함께 참조해야 해서
    /// 둘의 공통 조상인 Core가 유일한 자리이기 때문이다(AutoNextKey와 같은 사정).
    /// </summary>
    public const string KeepDisplayAwakeKey = "player.keepDisplayAwake";

    /// <summary>기본값 = true(영상 플레이어의 통상 동작 — A306 확정). 읽는 곳이 같은 값을 쓰도록 여기 둔다.</summary>
    public const bool KeepDisplayAwakeDefault = true;

    /// <summary>
    /// <see cref="KeepDisplayAwakeKey"/>가 바뀌면 열린 모든 영상 뷰가 억제 상태를 다시 맞추도록
    /// 알린다(설정 화면 → 각 VideoPlayerView). 발화·구독 모두 UI 스레드에서만 일어난다 —
    /// 이 앱은 창이 여럿이어도 UI 스레드가 하나다(WindowManager 주석).
    /// 구독자는 뷰 해체(Unloaded)에서 반드시 해제한다(정적 이벤트라 안 하면 뷰가 샌다).
    /// </summary>
    public static event Action? KeepDisplayAwakeChanged;

    /// <summary>설정 화면이 값을 저장한 직후 부른다(UiScale.NotifyChanged 관용구).</summary>
    public static void NotifyKeepDisplayAwakeChanged() => KeepDisplayAwakeChanged?.Invoke();
}
