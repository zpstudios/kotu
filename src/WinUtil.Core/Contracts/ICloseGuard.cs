namespace WinUtil.Core.Contracts;

/// <summary>
/// 미저장 변경이 있는 모듈 뷰의 닫힘 가드 계약 (A37).
/// 셸은 뷰를 교체(다른 파일 열기·모듈 전환·설정 진입)하거나 창을 닫기 전에
/// <see cref="HasUnsavedChanges"/>를 확인하고, 변경이 있으면
/// <see cref="ConfirmCloseAsync"/>로 사용자에게 저장/버리기/취소를 묻는다.
/// 다이얼로그 표시는 뷰 자신이 담당한다(셸은 결과만 본다).
/// </summary>
public interface ICloseGuard
{
    /// <summary>저장하지 않은 변경이 있는지.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// 미저장 상태가 바뀔 때 발생(true=미저장 있음). 셸은 창 제목의 ● 표시에 쓴다.
    /// UI 스레드 보장 없음 — 구독자가 디스패치한다.
    /// </summary>
    event Action<bool>? UnsavedChanged;

    /// <summary>
    /// 변경을 저장/버리기/취소 중 하나로 정리한다. true면 닫기(교체)를 계속 진행,
    /// false면 취소(현재 뷰 유지). 변경이 없으면 즉시 true.
    /// </summary>
    Task<bool> ConfirmCloseAsync();
}
