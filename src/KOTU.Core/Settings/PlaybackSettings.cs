namespace KOTU.Core.Settings;

/// <summary>
/// A258(v0.258.0): 영상·오디오 플레이어가 <b>함께 쓰는</b> 재생 옵션 키. 다른 재생 설정은
/// 모듈 접두어(video.* / audio.*)로 갈라 두지만 이 옵션만은 두 모듈이 한 값을 공유하기로
/// 확정돼(설정 화면의 토글도 하나뿐) 어느 모듈에도 속하지 않는 Core에 둔다 —
/// 두 모듈과 설정 화면이 모두 KOTU.Core를 참조하므로 상수 한 벌로 충분하다.
/// 쓰는 곳 = 설정 화면 토글 한 곳(즉시 Set+Save), 읽는 곳 = 두 플레이어의 EOF 전이뿐이다.
/// 캐시·변경 이벤트는 두지 않는다 — 파일이 끝날 때마다 라이브로 한 번 읽는 것으로 족하다.
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
}
