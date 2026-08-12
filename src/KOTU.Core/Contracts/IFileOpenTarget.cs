namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 "다음 파일은 모듈을 바꾸지 말고 나에게 달라"고 셸에 알리는 계약 (A59).
/// 지금 유일한 구현은 All Readable 통합 모듈 — 파일을 열어도 창은 그대로 두고
/// 센터와 하단 바만 그 확장자를 담당하는 자식 모듈 뷰로 갈아 끼우기 때문이다.
/// 셸(<c>MainWindow.OpenFile</c>)은 지금 보이는 뷰가 이 계약을 구현하면 라우팅보다 먼저 물어보고,
/// <see cref="TryOpenFile"/>가 false면(그 뷰가 다룰 수 없는 형식) 기존 라우팅으로 넘어간다.
/// 새 창으로 여는 경로(A24: Shift+더블클릭·우클릭 새 인스턴스·탐색기 더블클릭)는 이 계약을
/// 타지 않는다 — 새 창은 아직 뷰가 없어 그 파일의 전용 모듈로 열린다(현행 동작 유지).
/// </summary>
public interface IFileOpenTarget
{
    /// <summary>
    /// 파일을 이 뷰 안에서 연다. true = 내가 열었으니 셸은 모듈을 바꾸지 말 것,
    /// false = 내가 다룰 수 없으니 셸이 평소대로 라우팅할 것.
    /// 셸은 이 호출 전에 미저장 가드(<see cref="ICloseGuard"/>)를 이미 통과시킨다.
    /// </summary>
    bool TryOpenFile(string path);
}
