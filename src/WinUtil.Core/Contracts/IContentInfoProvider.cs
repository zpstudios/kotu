namespace WinUtil.Core.Contracts;

/// <summary>
/// 모듈 뷰가 현재 콘텐츠의 상세 정보(동영상=미디어 정보, 사진=EXIF 등)를 제공하는 계약 (v0.25.0).
/// 셸의 Ctrl 홀드 정보 오버레이가 호출한다. 구현하지 않은 모듈은 셸이 파일 기본 정보로 대신한다.
/// </summary>
public interface IContentInfoProvider
{
    /// <summary>현재 콘텐츠의 정보 텍스트(줄바꿈 구분). 콘텐츠가 없으면 null.</summary>
    Task<string?> GetContentInfoAsync();
}
