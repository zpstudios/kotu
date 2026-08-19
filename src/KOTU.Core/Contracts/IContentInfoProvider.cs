namespace KOTU.Core.Contracts;

/// <summary>
/// 정보 패널의 한 행 — 라벨·값 쌍 (A150). 셸이 하드웨어 우 패널과 같은
/// "라벨 고정폭 + 값" 2열 Grid로 그린다(정렬·복사를 위해 문자열 한 덩어리를 폐지).
/// 이 계약은 UI 프레임워크에 기대지 않는다(BCL만) — 모듈은 값 문자열만 내준다.
/// 표기는 <c>HardwareItem</c>(KOTU.Module.Hardware)과 같은 Label/Value 쌍 관례다.
/// </summary>
public sealed record ContentInfoItem(string Label, string Value)
{
    /// <summary>
    /// 그룹 구분 행(파일 정보/촬영 정보 사이 등) — 셸이 라벨·값 대신 여백으로 그린다.
    /// 하드웨어의 섹션 제목 대신 빈 행을 쓰는 이유: 정보 패널은 행이 십수 개라
    /// 제목 계층까지 두면 오히려 무거워진다(A150 구현 시 결정).
    /// </summary>
    public static ContentInfoItem Separator { get; } = new(string.Empty, string.Empty);

    /// <summary>이 행이 그룹 구분 행인지 — 셸 렌더러가 여백 처리에 쓴다.</summary>
    public bool IsSeparator => Label.Length == 0 && Value.Length == 0;
}

/// <summary>
/// 모듈 뷰가 현재 콘텐츠의 상세 정보(동영상=미디어 정보, 사진=EXIF 등)를 제공하는 계약 (v0.25.0).
/// 셸의 X 홀드 정보 오버레이가 호출한다. 구현하지 않은 모듈은 셸이 파일 기본 정보로 대신한다.
/// A150: 반환이 개행 구분 문자열에서 라벨·값 쌍 목록으로 바뀌었다 — 소비처는 셸 우측
/// 정보 패널(ContentInfoOverlay) 하나뿐이라 병행 메서드 없이 시그니처를 교체했다.
/// ⚠️ GPS 등 위치 EXIF는 싣지 않는다(개인정보 — 부록 B 69에서 기본 숨김 확정.
/// 표시 토글이 생기기 전까지는 수집 자체를 하지 않는 게 가장 안전하다).
/// </summary>
public interface IContentInfoProvider
{
    /// <summary>현재 콘텐츠의 정보 행 목록(위→아래 표시 순서). 콘텐츠가 없으면 null.</summary>
    Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync();
}
