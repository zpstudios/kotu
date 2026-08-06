namespace WinUtil.Core.Contracts;

/// <summary>
/// 모듈 뷰가 셸 하단 바에 얹을 컨트롤 줄을 제공할 때 구현한다(v0.21.0).
/// 동영상 트랜스포트 바처럼 뷰 자체 하단 줄과 셸 하단 바가 두 줄로 중복되던 것을
/// 셸 하단 바 한 줄로 통합하는 용도(실기기 피드백).
/// 반환 타입은 UI 프레임워크 비의존을 위해 object이며, 셸(WinUI 3)에서 UIElement로 캐스팅한다.
/// </summary>
public interface IBottomBarProvider
{
    /// <summary>
    /// 셸 하단 바에 얹을 요소를 뷰 트리에서 떼어내 반환한다. 없으면 null.
    /// 셸은 모듈 뷰를 띄운 직후 1회 호출한다.
    /// </summary>
    object? TakeBottomBar();
}
