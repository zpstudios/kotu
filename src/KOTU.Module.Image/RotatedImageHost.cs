using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace KOTU.Module.Image;

/// <summary>
/// 단일 Image의 회전 후 경계를 ScrollViewer에 알리는 로컬 레이아웃 호스트.
/// Image의 네이티브 측정과 기존 RenderTransform을 유지하며 메타데이터를 읽지 않는다.
/// </summary>
public sealed class RotatedImageHost : Panel
{
    private bool _swapsAxes;

    internal bool SwapsAxes
    {
        get => _swapsAxes;
        set
        {
            if (_swapsAxes == value) return;
            _swapsAxes = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return new Size();
        var child = Children[0];
        child.Measure(RotatedImageLayout.SwapAxes(availableSize, _swapsAxes));
        return RotatedImageLayout.SwapAxes(child.DesiredSize, _swapsAxes);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count > 0)
        {
            var child = Children[0];
            // 회전 전 슬롯 자체를 중앙에 둔다. 변환 후 경계와 호스트 경계가 일치한다.
            child.Arrange(RotatedImageLayout.CenterChild(child.DesiredSize, finalSize));
        }
        return finalSize;
    }
}
