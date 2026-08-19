namespace KOTU.Core.Integration;

/// <summary>
/// 바탕화면 배경 지정 훅 (A161, v0.174.0). 모듈은 Core에만 의존한다는 아키텍처 규칙과
/// "모듈 프로젝트에는 DllImport를 두지 않는다"(전부 셸에 격리 — WindowMinSize·TrayIcon·
/// ExplorerIntegration·TaskbarIdentity) 규약 때문에, 이미지 모듈의 우클릭 메뉴
/// "Set as desktop background"가 셸의 구현(KOTU.App.Integration.DesktopWallpaper —
/// user32 P/Invoke + HKCU 레지스트리 쓰기)을 직접 부를 수 없다.
/// 셸(App)이 시작 시 <see cref="Setter"/>를 배선하고, 모듈은 <see cref="TrySet"/>만 부른다 —
/// <see cref="AdminRelaunchHook"/>·<see cref="RestartSession"/>과 같은 배선 방식이다.
///
/// 인자 = 배경으로 걸 이미지 파일의 전체 경로. <b>PNG 변환과 파일 쓰기는 부르는 쪽</b>
/// (이미지 뷰의 전용 워커, A42)이 이미 끝낸 상태여야 한다 — 이 훅은 "이 파일을 걸어라"만 한다.
///
/// A124의 두 훅과 달리 <b>결과를 돌려준다</b>: 사용자가 직접 누른 동작이라 성공·실패를 알려야
/// 하기 때문이다(안내는 이미지 뷰 하단 바가 한다). 실패는 전부 false로 접히고 예외는 새지 않는다.
/// </summary>
public static class DesktopWallpaperHook
{
    /// <summary>
    /// 셸이 배선하는 배경 지정 동작. A124 훅들과 달리 <b>UI 스레드가 아니라</b> 모듈의
    /// 뷰 전용 워커 스레드에서 호출된다(변환·파일 쓰기와 같은 워커에서 이어 부른다).
    /// </summary>
    public static Func<string, bool>? Setter { get; set; }

    /// <summary>
    /// 이미지를 바탕화면 배경으로 건다. 배선 전(이론상 도달 불가 — 셸이 첫 창을 만들기 전에
    /// 배선한다)이거나 실패하면 false. 예외도 false로 접는다.
    /// </summary>
    public static bool TrySet(string imagePath)
    {
        try { return Setter?.Invoke(imagePath) ?? false; }
        catch { return false; /* 배경 지정 실패가 뷰를 죽이면 안 된다 — 안내는 호출 쪽 몫 */ }
    }
}
