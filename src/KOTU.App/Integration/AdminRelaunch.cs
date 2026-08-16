using Microsoft.UI.Xaml;

namespace KOTU.App.Integration;

/// <summary>
/// 관리자 권한 재시작(runas) 공용 흐름 (A94 4차, v0.151.0 — A17/A124가 하드웨어 뷰
/// OnElevateClick에 두었던 구현을 단계 변경 없이 그대로 추출한 것).
///
/// 부르는 곳 2곳:
/// ① 하드웨어 뷰의 "Restart as admin" 버튼 — 모듈은 Core에만 의존하므로
///    <see cref="KOTU.Core.Integration.AdminRelaunchHook"/> 훅(App이 배선)을 거쳐 온다.
/// ② 탐색기 파일 조작의 접근 거부 안내(ExplorerDialogs) — 같은 App 프로젝트라 직접 부른다.
///
/// 단계(추출 전과 동일한 순서·동일한 조건):
/// ⓐ 실행 파일 경로를 못 얻으면 아무것도 하지 않는다.
/// ⓑ A124 — 창이 전부 살아 있는 지금 창 세트를 세션 파일로 기록(실패는 훅이 삼킨다).
/// ⓒ 단일 인스턴스 키를 먼저 반납한다 — 안 그러면 새(관리자) 프로세스가 이 인스턴스로
///    리다이렉트되어 죽는다.
/// ⓓ runas로 재기동. 실패(UAC 취소)면 세션 파일을 되지우고 키를 되찾은 뒤 그대로 돌아간다.
/// ⓔ 성공이면 호출부 정리 콜백(하드웨어 = 드라이버 핸들)을 부르고 프로세스를 내린다.
/// </summary>
internal static class AdminRelaunch
{
    /// <summary>
    /// 단일 인스턴스 키 — Program.InstanceKey와 반드시 같은 값이어야 한다(그쪽은 private).
    /// 같은 조립식(Branding.AppName + "-Main")을 써 리브랜딩 때 함께 움직이게 한다.
    /// </summary>
    private const string InstanceKey = Branding.AppName + "-Main";

    /// <summary>
    /// 관리자 권한으로 재시작한다(위 단계 ⓐ~ⓔ). UI 스레드에서 부를 것 —
    /// 마지막 단계가 Application.Exit다. beforeExit = 종료 직전 정리(없으면 생략).
    /// </summary>
    internal static void Relaunch(Action? beforeExit = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        // A124: 창이 전부 살아 있는 지금(재시작 확정 전) 창 세트를 기록한다. 미저장 가드(A37)는
        // 현행 그대로 타지 않는다 — 이 경로는 원래 묻지 않고 내려간다(Application.Exit).
        KOTU.Core.Integration.RestartSession.TryWrite();

        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch
        {
            // UAC 취소 — 재시작 무산: 방금 쓴 세션 파일을 되지우고(A124),
            // 유일한 인스턴스이므로 키를 되찾는다 (Program.InstanceKey와 동일해야 함)
            KOTU.Core.Integration.RestartSession.TryDiscard();
            Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(InstanceKey);
            return;
        }
        beforeExit?.Invoke(); // 하드웨어 뷰: 드라이버 핸들을 먼저 정리하고 내려간다
        Application.Current.Exit();
    }
}
