namespace KOTU.Core.Integration;

/// <summary>
/// 관리자 재시작(A124) 창 세트 스냅샷 훅. 모듈은 Core에만 의존한다는 아키텍처 규칙 때문에
/// 하드웨어 모듈(Restart as admin 버튼)은 셸의 WindowManager를 직접 부를 수 없다.
/// 셸(App)이 시작 시 <see cref="Writer"/>·<see cref="Discarder"/>를 배선하고,
/// 모듈은 Try 계열만 부른다.
///
/// 쓰는 쪽 = 하드웨어 모듈의 "Restart as admin"(runas) 직전 한 곳뿐이다. 일반 재시작·일반
/// 시작은 세션 파일을 쓰지 않으므로, 읽는 쪽 복원(WindowManager.TryRestoreSession)이 실질
/// 발동하는 것도 승격 재기동뿐이다.
///
/// 실패는 전부 조용히 무시(TaskbarIdentity/A105 관례) — 스냅샷을 못 써도 재시작 자체는
/// 종전(기본 1창 시작) 그대로 진행돼야 한다.
/// </summary>
public static class RestartSession
{
    /// <summary>셸이 배선하는 창 세트 직렬화 동작. UI 스레드에서 호출된다.</summary>
    public static Action? Writer { get; set; }

    /// <summary>셸이 배선하는 세션 파일 삭제 동작 — UAC 취소로 재시작이 무산됐을 때의 뒷정리.</summary>
    public static Action? Discarder { get; set; }

    /// <summary>열린 창 세트를 세션 파일로 기록한다. 실패해도 던지지 않는다.</summary>
    public static void TryWrite()
    {
        try { Writer?.Invoke(); }
        catch { /* 직렬화 실패 → 세션 파일 없이 종전대로 재시작(A124 폴백) */ }
    }

    /// <summary>기록해 둔 세션 파일을 지운다. 실패해도 던지지 않는다(2분 기한이 잔재를 무효화한다).</summary>
    public static void TryDiscard()
    {
        try { Discarder?.Invoke(); }
        catch { /* 삭제 실패도 조용히 — 읽는 쪽 기한 검사가 안전망이다 */ }
    }
}
