using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KOTU.Ui;

/// <summary>네 맞춤 방식은 버튼과 메뉴에서 같은 좌표·크기·중심선을 쓴다.</summary>
internal static class FitIcons
{
    internal static Grid BuildOriginalRatioBox() => ToolbarIcons.Build(ToolbarIconKind.OriginalRatio);
    internal static PathIcon BuildOriginalRatioIcon() => ToolbarIcons.BuildMenu(ToolbarIconKind.OriginalRatio);

    internal static void ConfigureMenu(MenuFlyoutItem original, MenuFlyoutItem contain,
        MenuFlyoutItem width, MenuFlyoutItem height)
    {
        var lettering = (FontFamily)Application.Current.Resources["KotuControlFontFamily"];
        ConfigureMenuItem(original, ToolbarIconKind.OriginalRatio, lettering);
        ConfigureMenuItem(contain, ToolbarIconKind.FitContain, lettering);
        ConfigureMenuItem(width, ToolbarIconKind.FitWidth, lettering);
        ConfigureMenuItem(height, ToolbarIconKind.FitHeight, lettering);
    }

    private static void ConfigureMenuItem(MenuFlyoutItem item, ToolbarIconKind kind, FontFamily lettering)
    {
        item.Icon = ToolbarIcons.BuildMenu(kind);
        item.FontFamily = lettering;
        // WinUI의 16px 아이콘 슬롯을 네 항목 모두 24px로 맞춘다.
        // 텍스트 시작 위치28과 행 높이는 유지해 아이콘 뒤4px 여유를 둔다.
        item.Loaded += (_, _) =>
        {
            if (FindIconRoot(item) is { } root)
            {
                root.Width = root.Height = ToolbarIconGeometry.ViewportSize(kind);
                // 24px 뷰포트의 중심을 메뉴 본문 중심에 맞춘다.
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
