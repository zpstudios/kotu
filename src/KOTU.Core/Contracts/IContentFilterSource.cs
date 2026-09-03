namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 "지금 화면에 얹힌 콘텐츠의 실효 확장자 집합은 이것"이라고 셸에 알리는 계약 (A331).
/// 좌측 파일 리스트(A57 ③)와 그 리스트를 원본으로 삼는 표면(중앙 썸네일·S4 그리드)의 필터는
/// 원래 <see cref="IModule.SupportedExtensions"/> 하나로 정해졌는데, All Readable(A59)처럼
/// <b>한 모듈 안에서 콘텐츠 종류가 갈리는</b> 뷰에서는 그 값(전 모듈 합집합)이 지금 보고 있는
/// 콘텐츠와 어긋난다 — 음악 파일을 열어 센터·하단 바가 오디오 자식으로 갈렸는데도 좌 리스트에는
/// 사진·문서가 그대로 남아 있는 사용자 보고(2026-09-03)가 그 어긋남이다.
///
/// 계약 규칙:
///  · null을 돌려주면 "실효 집합 없음" = 셸이 종전대로 모듈의 담당 확장자를 쓴다(무회귀 기본값).
///  · 값은 <b>동기·즉시</b>여야 한다 — 셸이 표시 종착점(ApplyOverlayStates)에서 읽는다.
///  · 변화 통지 이벤트는 두지 않는다: 이 값이 갈리는 시점(자식 교체)은 언제나 셸의 콘텐츠 전환
///    (MainWindow.OpenFile → SetContentState)이 뒤따르는 자리라, 그 전환이 곧 갱신 시점이다.
///    이벤트를 두면 "필터 변경 → 리스트 재항해 → 다시 필터 질의"의 재진입 고리가 생긴다.
/// </summary>
public interface IContentFilterSource
{
    /// <summary>
    /// 지금 콘텐츠 기준의 담당 확장자(소문자·점 포함) — 없으면 null(모듈 기본값 사용).
    /// </summary>
    IReadOnlyList<string>? ContentExtensions { get; }
}
