namespace KOTU.Core.Contracts;

/// <summary>일시적인 시각 트리 분리와 달리, 소유자가 콘텐츠를 영구히 버릴 때 호출한다.</summary>
public interface IContentLifetime
{
    void ReleaseContent();
}
