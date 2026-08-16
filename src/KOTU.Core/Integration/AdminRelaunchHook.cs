namespace KOTU.Core.Integration;

/// <summary>
/// 관리자 권한 재시작(runas) 훅 (A94 4차, v0.151.0). 모듈은 Core에만 의존한다는 아키텍처 규칙 때문에
/// 하드웨어 모듈("Restart as admin" 버튼)이 셸의 재시작 구현(KOTU.App.Integration.AdminRelaunch —
/// AppInstance·Application.Exit 같은 셸 API를 쓴다)을 직접 부를 수 없다.
/// 셸(App)이 시작 시 <see cref="Relauncher"/>를 배선하고, 모듈은 <see cref="Relaunch"/>만 부른다 —
/// <see cref="RestartSession"/>과 같은 배선 방식이다.
///
/// 인자 = 종료 직전 정리 콜백. 하드웨어 모듈은 센서 드라이버 핸들 정리(SensorService.Shutdown)를
/// 넘긴다 — 재시작이 실제로 확정된(runas 성공) 뒤에만 불린다.
///
/// 실패를 삼키지 않는 것은 의도다(A17/A124 추출 전 코드와 동일): runas 실패는 구현부가
/// 자체 복구(세션 파일 되지우기 + 인스턴스 키 되찾기)하고, 그 밖의 예외는 종전처럼 위로 나간다.
/// </summary>
public static class AdminRelaunchHook
{
    /// <summary>셸이 배선하는 관리자 재시작 동작. UI 스레드에서 호출된다.</summary>
    public static Action<Action?>? Relauncher { get; set; }

    /// <summary>관리자 권한으로 재시작한다. 배선 전(이론상 도달 불가)이면 무동작.</summary>
    public static void Relaunch(Action? beforeExit = null) => Relauncher?.Invoke(beforeExit);
}
