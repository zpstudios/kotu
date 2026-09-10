using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KOTU.Ui;

/// <summary>원본 배율 표시는 버튼과 메뉴 모두 같은 좌표 도형을 쓴다. 글꼴·기준선에 의존하지 않는다.</summary>
internal static class FitIcons
{
    internal static Grid BuildOriginalRatioBox() => ToolbarIcons.Build(ToolbarIconKind.OriginalRatio);
    internal static PathIcon BuildOriginalRatioIcon() => ToolbarIcons.BuildMenu(ToolbarIconKind.OriginalRatio);

    internal static void ConfigureOriginalMenuItem(MenuFlyoutItem item)
    {
        item.Icon = BuildOriginalRatioIcon();
        // WinUI 1.8 기본 메뉴는 아이콘을 16×16 Viewbox로 줄인다. Original만 24로 넓히고
        // 글자는 기본 위치28을 유지한다. 24폭 아이콘 뒤4px 여유이며 다른 항목과 글자 정렬이 같다.
        item.Loaded += (_, _) =>
        {
            if (FindIconRoot(item) is { } root)
            {
                root.Width = root.Height = ToolbarIconGeometry.ViewportSize(ToolbarIconKind.OriginalRatio);
                // 측정 높이는 기존16을 유지한다. 잉크18은 본문 높이20의 중앙에 들어간다.
                root.Margin = new Thickness(0, -4, 0, -4);
            }
        };
    }

    private static Viewbox? FindIconRoot(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Viewbox { Name: "IconRoot" } box) return box;
            if (FindIconRoot(child) is { } nested) return nested;
        }
        return null;
    }
}
