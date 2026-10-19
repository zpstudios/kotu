namespace KOTU.Core.Contracts;

/// <summary>분리된 하단 바와 통합 뷰의 이미지 복사 키를 현재 이미지에 전달한다.</summary>
public interface IImageCopyTarget
{
    /// <summary>focusScope는 통합 호스트 자신에 포커스가 있을 때만 허용하는 조상 객체다.</summary>
    bool TryCopyImage(bool repeat = false, object? focusScope = null);
}
