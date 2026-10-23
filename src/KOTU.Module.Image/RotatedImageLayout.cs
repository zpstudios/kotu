using Windows.Foundation;

namespace KOTU.Module.Image;

/// <summary>이미지의 측정 축과 회전 전 배치 사각형만 계산한다. 픽셀 변환은 하지 않는다.</summary>
internal static class RotatedImageLayout
{
    internal static Size SwapAxes(Size size, bool swapsAxes) =>
        swapsAxes ? new Size(size.Height, size.Width) : size;

    internal static Rect CenterChild(Size childSize, Size hostSize) => new(
        (hostSize.Width - childSize.Width) / 2,
        (hostSize.Height - childSize.Height) / 2,
        childSize.Width,
        childSize.Height);
}
