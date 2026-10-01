namespace KOTU.Core.Contracts;

/// <summary>
/// 셸이 미리 반영한 파일 경로를 실제 열기 실패 뒤 빈 상태로 되돌리도록 알리는 계약.
/// 뷰는 오류 문구와 내부 상태를 먼저 확정한 뒤 통지하며, 셸은 뷰를 교체하지 않는다.
/// </summary>
public interface IContentOpenFailedSource
{
    /// <summary>요청한 파일을 열지 못해 열린 콘텐츠가 없으면 발생한다(UI 스레드 보장 없음).</summary>
    event Action? ContentOpenFailed;
}
