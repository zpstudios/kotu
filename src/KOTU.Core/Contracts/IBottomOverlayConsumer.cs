namespace KOTU.Core.Contracts;

/// <summary>콘텐츠 맞춤에 필요한 셸 하단 오버레이를 전달한다. UI 프레임워크 타입은 소비자가 해석한다.</summary>
public interface IBottomOverlayConsumer
{
    void SetBottomOverlay(object overlay);
}
