namespace KOTU.Core.Contracts;

/// <summary>
/// A332: 모듈 뷰가 <b>지금 열려 있는 콘텐츠의 상세 정보가 갱신됐다</b>(= 다시 물어보면 값이
/// 달라진다)고 셸에 알리는 계약. <see cref="IContentPathChangedSource"/>(A279)와 같은 모양의
/// "뷰 → 셸" 통지이고, 셸은 이 신호에서만 정보 패널의 <b>열림 축 캐시</b>를 비우고 다시 묻는다.
///
/// 필요한 이유: 정보 패널은 파일당 1회만 provider를 부르고 결과를 캐시한다. 그런데 그 유일한
/// 조회가 최악의 시점에 일어난다 — 재생 뷰는 <c>Play</c> 직후 같은 프레임에 ContentOpened를
/// 쏘고, 셸은 그 발화를 받아 즉시 정보를 묻는다. libvlc가 파일을 막 연 시점이라 셸 속성 조회가
/// 빈 값을 돌려주고 플레이어 폴백도 아직 아는 게 없어, 빈칸 스냅샷이 캐시에 굳어 버렸다
/// (재생 중인데 Duration·Bit rate·Sample rate가 전부 빈칸 — 사용자 보고 2026-09-03).
///
/// 계약 규칙:
///  · 발화는 <b>값이 실제로 갈린 시점 1회</b>여야 한다(libvlc <c>LengthChanged</c>처럼 파싱이
///    끝나는 자리). 같은 파일에서 반복 발화하면 셸이 그만큼 다시 조회한다 — 재생 뷰는 파일당
///    1회로 스스로 결박한다.
///  · <b>재진입 금지</b>: 셸의 재조회 경로(GetContentInfoAsync)는 이 이벤트를 쏘지 않는다.
///    통지 → 재조회 → (또 통지)의 고리가 성립하지 않아야 계약이 안전하다.
///  · UI 스레드 보장이 없다(libvlc 이벤트 스레드에서 온다) — 셸이 디스패치한다.
/// </summary>
public interface IContentInfoChangedSource
{
    /// <summary>열려 있는 콘텐츠의 상세 정보가 갱신됐다(UI 스레드 보장 없음).</summary>
    event Action? ContentInfoChanged;
}
