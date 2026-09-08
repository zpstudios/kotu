using Microsoft.UI.Xaml.Controls;

namespace KOTU.Ui;

/// <summary>원본 배율 표시는 버튼과 메뉴 모두 같은 좌표 도형을 쓴다. 글꼴·기준선에 의존하지 않는다.</summary>
internal static class FitIcons
{
    internal static Grid BuildOriginalRatioBox() => ToolbarIcons.Build(ToolbarIconKind.OriginalRatio);
    internal static PathIcon BuildOriginalRatioIcon() => ToolbarIcons.BuildMenu(ToolbarIconKind.OriginalRatio);
}
