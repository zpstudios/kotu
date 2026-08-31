namespace KOTU.Core.Integration;

/// <summary>
/// 재생 중 화면보호기·디스플레이 꺼짐 억제 훅 (A306, v0.290.0). 모듈은 Core에만 의존한다는
/// 아키텍처 규칙과 "모듈 프로젝트에는 DllImport를 두지 않는다"(전부 셸에 격리 —
/// WindowMinSize·TrayIcon·AltTabExclusion·DesktopWallpaper) 규약 때문에, 영상 모듈이
/// 셸의 구현(KOTU.App.Integration.DisplayAwake — kernel32 SetThreadExecutionState)을
/// 직접 부를 수 없다. 셸(App)이 시작 시 <see cref="Setter"/>를 배선하고, 모듈은
/// <see cref="Acquire"/>/<see cref="Release"/>만 부른다 —
/// <see cref="DesktopWallpaperHook"/>·<see cref="DefaultAudioInputHook"/>과 같은 배선 방식이다.
///
/// <b>왜 이 훅이 개수를 세는가</b>: SetThreadExecutionState는 <b>스레드 단위</b> 상태다.
/// 이 앱은 창이 여럿이어도 UI 스레드가 하나뿐이라(WindowManager 주석 — 모든 창은 같은 스레드에서
/// 만들어진다) 영상 창 A가 재생 중인데 창 B가 자기 몫을 해제하면 <b>A의 억제까지 풀려 버린다</b>.
/// 그래서 실제 API 호출은 0에서 1로 올라갈 때·1에서 0으로 내려갈 때만 하고, 그 사이 창들의
/// 켜고 끔은 여기 카운터가 흡수한다.
///
/// 계약: 부르는 쪽은 <b>UI 스레드에서</b>, 자기 상태 플래그로 <see cref="Acquire"/>와
/// <see cref="Release"/>를 <b>1:1로 짝지어</b> 부른다(뷰 해체에서의 해제 포함). 단일 스레드
/// 전용이라 카운터에 잠금을 두지 않는다. 짝이 어긋나도 카운터는 0 밑으로 내려가지 않는다.
/// 실패(배선 전·API 실패)는 전부 조용히 무시한다 — 억제는 보조 기능이고 재생을 막으면 안 된다(A306 확정).
/// </summary>
public static class DisplayAwakeHook
{
    /// <summary>
    /// 셸이 배선하는 억제 적용 동작. 인자 true = 억제 시작, false = 원상 복귀.
    /// UI 스레드에서만 불린다 — SetThreadExecutionState가 스레드 단위라 건 스레드와
    /// 푸는 스레드가 같아야 하기 때문이다(위 요약 참고).
    /// </summary>
    public static Action<bool>? Setter { get; set; }

    /// <summary>지금 억제를 요구하고 있는 뷰의 수. UI 스레드 전용이라 잠금 없이 센다.</summary>
    private static int _holders;

    /// <summary>억제 요구 1건 추가. 처음 1건이 될 때만 실제 API를 부른다.</summary>
    public static void Acquire()
    {
        if (++_holders == 1) Apply(true);
    }

    /// <summary>억제 요구 1건 해제. 마지막 1건이 빠질 때만 실제 API를 부른다.</summary>
    public static void Release()
    {
        if (_holders == 0) return; // 짝이 어긋난 해제 — 무시(카운터를 음수로 만들지 않는다)
        if (--_holders == 0) Apply(false);
    }

    private static void Apply(bool keepAwake)
    {
        try
        {
            Setter?.Invoke(keepAwake);
        }
        catch
        {
            // 억제 실패가 재생을 죽이면 안 된다 — 로그도 남기지 않는다(A306 확정).
        }
    }
}
