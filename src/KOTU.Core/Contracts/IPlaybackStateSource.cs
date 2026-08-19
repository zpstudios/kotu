namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 셸에 재생 상태를 알리는 계약 (A186 — 영상 하단 바 자동 숨김의 신호원).
/// 셸은 "재생 표면 + 재생 중 + 무입력 3초"면 하단 바를 자동 숨김하고, 일시정지·정지·입력에서
/// 되살린다 — 판단·타이머는 전부 셸(MainWindow) 몫이고 뷰는 상태만 내준다.
/// 구현: 영상 모듈(VideoPlayerView)과 그 호스트(AllReadableView — 자식 중계)뿐이다.
/// 오디오 등 다른 모듈로의 확대는 후속 판단(A186 등재문 — "영상 모듈 한정").
/// </summary>
public interface IPlaybackStateSource
{
    /// <summary>
    /// 재생 표면(영상)이 실제로 전면에 있는가. 영상 뷰 자신은 항상 true,
    /// 호스트 뷰(All Readable)는 자식이 이 계약을 구현할 때만 true —
    /// 계약 구현 여부만으로는 "지금 영상인가"를 알 수 없어서 두는 축이다
    /// (All Readable은 문서·사진 자식을 얹고 있어도 이 인터페이스를 구현한다).
    /// </summary>
    bool HasPlaybackSurface { get; }

    /// <summary>지금 재생 중인가 — 일시정지·정지·미디어 없음이면 false.</summary>
    bool IsPlaying { get; }

    /// <summary>재생/일시정지/정지 전이 시 발생한다(UI 스레드 보장 없음 — 다른 계약들과 동일).</summary>
    event Action? PlaybackStateChanged;
}
