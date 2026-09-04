namespace KOTU.Core.Contracts;

/// <summary>
/// 셸의 시스템 미디어 컨트롤(SMTC — Windows 미디어 플라이아웃·키보드 미디어 키·헤드셋 버튼)이
/// 조작하는 재생 표면 계약 (A349 배치 3 — 사양 = docs/A349-media-keys-research.md §4.1).
/// <para>
/// 다른 계약과의 관계:
/// ① <see cref="IPlaybackStateSource"/>를 상속한다 — SMTC는 <c>PlaybackStatus</c>를 계속
///    갱신해야 이벤트가 오므로(조사 §3-①) 재생 여부와 그 전이 통지가 곧 이 계약의 전제다.
///    <see cref="IPlaybackStateSource.HasPlaybackSurface"/>는 A186(하단 바 자동 숨김)의 축
///    그대로다 — 셸은 그 값을 SMTC 메타데이터 종류(영상 = Video / 그 밖 = Music)를 고르는 데도 쓴다.
/// ② 파일이 바뀐 사실은 <see cref="IContentStateSource.ContentOpened"/>(로드 완료)로 받는다 —
///    A279 <see cref="IContentPathChangedSource"/>·A348 <see cref="ICurrentPathSource"/>는
///    로드 앞 통지라 플라이아웃 제목이 실제 재생보다 앞서 튄다(셸이 쓰지 않는다).
/// ③ A346 <see cref="IBrowseOrderConsumer"/>가 정한 좌 리스트 순서가 곧 이웃 순서다 —
///    <see cref="Next"/>/<see cref="Previous"/>는 하단 바 ⏮/⏭·Ctrl+←/→와 같은 경로를 탄다.
/// </para>
/// <para>
/// 정지(Stop) 버튼은 두지 않는다 — 저장소에 "정지" 개념이 따로 없어(일시정지만 있다)
/// 셸이 SMTC의 Stop을 <see cref="Pause"/>로 접는다(조사 §4.3).
/// </para>
/// <para>
/// 스레드: 셸은 SMTC의 <c>ButtonPressed</c>(비UI 스레드로 온다 — 조사 §3-②)를 받아
/// 디스패처로 UI 스레드에 넘긴 뒤에만 이 계약의 메서드·속성을 만진다. 즉 <b>구현 쪽은
/// UI 스레드 호출만 가정하면 된다</b>. 반대로 이벤트 두 개(<see cref="NeighborsChanged"/>와
/// 상속한 <see cref="IPlaybackStateSource.PlaybackStateChanged"/>)는 다른 계약들과 같이
/// UI 스레드 보장이 없다 — 셸이 디스패치한다.
/// </para>
/// 구현: 영상·오디오 재생 뷰와 그 호스트(AllReadableView — 자식 중계).
/// </summary>
public interface IMediaTransportTarget : IPlaybackStateSource
{
    /// <summary>
    /// 지금 이 뷰가 <b>실제로 미디어 키를 받을 재생 표면을 갖는가</b>. 셸은 이 값이 거짓인 동안
    /// SMTC 세션을 비활성으로 접어 둔다(미디어 플라이아웃에 KOTU가 뜨지 않고, 다른 플레이어의
    /// 미디어 키를 빼앗지도 않는다).
    /// <para>
    /// <see cref="IPlaybackStateSource.HasPlaybackSurface"/>와 <b>뜻이 다르다</b> —
    /// 그쪽은 A186(하단 바 자동 숨김)의 축이라 <b>영상 표면인가</b>를 묻고 오디오는 거짓이다
    /// (셸은 그 값을 SMTC 메타데이터 종류 Video/Music 선택에만 쓴다). 이쪽은 <b>미디어 키를
    /// 받을 자격이 있는가</b>라서 오디오도 참이다.
    /// </para>
    /// 값: 영상 뷰·오디오 뷰는 항상 참. 호스트 뷰(All Readable)는 <b>자식이 이 계약을 구현할 때만</b>
    /// 참이다 — 문서·사진·압축 자식을 얹고 있어도 호스트 자신은 이 인터페이스를 구현하기 때문에
    /// (<see cref="IPlaybackStateSource.HasPlaybackSurface"/>가 같은 이유로 존재하는 것과 동형),
    /// 이 축이 없으면 PDF를 열어도 셸이 SMTC 세션을 붙여 버린다.
    /// 값이 갈리는 시점(자식 교체)은 <see cref="NeighborsChanged"/>로 통지된다.
    /// </summary>
    bool HasMediaTransport { get; }

    /// <summary>
    /// 이전 파일로 갈 수 있는가 — 하단 바 ⏮ 활성 판정(UpdateNeighborButtons)과 같은 식이다
    /// (이웃이 있거나, 목록 루프 + 2개 이상이라 끝에서 되감을 수 있을 때).
    /// </summary>
    bool CanPrevious { get; }

    /// <summary>다음 파일로 갈 수 있는가 — <see cref="CanPrevious"/>와 같은 판정의 반대쪽.</summary>
    bool CanNext { get; }

    /// <summary>이전 파일로 이동(하단 바 ⏮·Ctrl+←와 같은 경로). 갈 곳이 없으면 무동작.</summary>
    void Previous();

    /// <summary>다음 파일로 이동(하단 바 ⏭·Ctrl+→와 같은 경로). 갈 곳이 없으면 무동작.</summary>
    void Next();

    /// <summary>재생 중이 아니면 재생한다(이미 재생 중이면 무동작 — 토글이 아니다).</summary>
    void Play();

    /// <summary>재생 중이면 일시정지한다(아니면 무동작 — 토글이 아니다). SMTC Stop도 여기로 온다.</summary>
    void Pause();

    /// <summary>
    /// <see cref="HasMediaTransport"/>·<see cref="CanPrevious"/>/<see cref="CanNext"/>가
    /// 달라졌을 수 있다 — 셸이 세션 활성 여부와 SMTC의
    /// 이전/다음 버튼 활성을 다시 계산한다. 발화 지점은 하단 바 버튼 활성을 다시 계산하는
    /// 곳 전부(UpdateNeighborButtons 끝)로 맞춰, 두 표면이 같은 시점에 같은 값을 얻는다.
    /// UI 스레드 보장 없음(다른 계약들과 동일).
    /// </summary>
    event Action? NeighborsChanged;
}
