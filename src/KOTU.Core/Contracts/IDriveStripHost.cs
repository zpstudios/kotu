namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 자기 하단 바에 셸이 만든 공용 드라이브 줄을 받아 끼울 때 구현한다 (A22, v0.108.0).
/// 표시 컨트롤은 셸(KOTU.App.Controls.DriveStrip)에 하나만 두고 모듈이 자리를 내주는 방식 —
/// 모듈 프로젝트는 셸을 참조할 수 없어(App → 모듈 단방향) 타입은 IBottomBarProvider와 같은
/// 이유로 object이며, 모듈은 UIElement로 캐스팅해 슬롯에 넣기만 한다.
/// 보임/숨김 판단(파일이 열려 있으면 숨긴다)은 셸이 한다 — 파일 유무를 이미 아는 곳이 셸이다.
/// </summary>
public interface IDriveStripHost
{
    /// <summary>하단 바의 드라이브 줄 슬롯에 컨트롤을 끼운다. 뷰 생성 직후 셸이 1회 호출한다.</summary>
    void AttachDriveStrip(object strip);

    /// <summary>
    /// 드라이브 줄을 띄울지(= 파일이 열려 있지 않은지). 모듈은 같은 칸을 쓰는 자기 요소
    /// (파일명·상태 텍스트)를 함께 비켜준다.
    /// </summary>
    void ShowDriveStrip(bool show);
}
