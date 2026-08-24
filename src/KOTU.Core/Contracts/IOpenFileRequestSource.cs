namespace KOTU.Core.Contracts;

/// <summary>
/// A223(2026-08-24): 모듈 뷰가 자기 하단 바에서 파일 열기를 <b>요청</b>할 때 셸에 위임하는 계약 —
/// 문서 모듈 Open 버튼(FileOpenPicker)이 첫 소비자다. 뷰가 직접 열지 않고 셸에 넘기는 이유:
/// 열기 경로의 미저장 가드(A37 ConfirmDiscardAsync)·제목 갱신·재사용 규칙이 전부 셸
/// OpenFile에 모여 있고, 뷰가 자체 열기(OpenAny)를 하면 그 가드를 전부 우회한다.
/// 방향은 다른 이벤트 계약과 같은 뷰 → 셸 단방향(<see cref="IContentStateSource"/> 관용구).
/// </summary>
public interface IOpenFileRequestSource
{
    /// <summary>사용자가 고른 파일 경로로 열기를 요청한다(UI 스레드 보장 없음 — 셸이 디스패치).</summary>
    event Action<string>? OpenFileRequested;
}
