namespace KOTU.Ui;

internal enum ToolbarIconKind { Play, Pause, Previous, Next, Sound, Muted, OriginalRatio }
internal readonly record struct IconPoint(double X, double Y);

/// <summary>하단 바 도형의 단일 좌표 원본. 18×18 안의 채움 윤곽이며 UI·글꼴에 의존하지 않는다.</summary>
internal static class ToolbarIconGeometry
{
    internal const double Size = 18;

    internal static IconPoint[][] Create(ToolbarIconKind kind) => kind switch
    {
        // 삼각형은 질량이 왼쪽에 몰리므로 반 픽셀보다 조금 오른쪽으로 둔다.
        ToolbarIconKind.Play => [Polygon(5, 2, 16, 9, 5, 16)],
        ToolbarIconKind.Pause => [Box(4, 2, 3, 14), Box(11, 2, 3, 14)],
        ToolbarIconKind.Previous => [Box(2, 2, 2, 14), Polygon(16, 2, 5, 9, 16, 16)],
        ToolbarIconKind.Next => [Box(14, 2, 2, 14), Polygon(2, 2, 13, 9, 2, 16)],
        ToolbarIconKind.Sound =>
        [
            Speaker(),
            Polygon(9, 6, 10, 5, 14, 9, 10, 13, 9, 12, 12, 9),
            Polygon(12, 3, 13, 2, 18, 9, 13, 16, 12, 15, 16, 9),
        ],
        ToolbarIconKind.Muted =>
        [
            Speaker(),
            Polygon(10, 5, 13, 8, 16, 5, 17, 6, 14, 9, 17, 12, 16, 13, 13, 10, 10, 13, 9, 12, 12, 9, 9, 6),
        ],
        ToolbarIconKind.OriginalRatio =>
        [
            // 바깥과 안쪽 윤곽은 짝홀 채움으로 테두리를 만든다. 메뉴·버튼 모두 같은 도형이다.
            Polygon(3, 3, 15, 3, 17, 5, 17, 13, 15, 15, 3, 15, 1, 13, 1, 5),
            Polygon(3, 4, 2, 5, 2, 13, 3, 14, 15, 14, 16, 13, 16, 5, 15, 4),
            One(4), One(11), Box(8.5, 7, 1, 1), Box(8.5, 10, 1, 1),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static IconPoint[] Speaker() => Polygon(1, 7, 3, 7, 7, 3, 7, 15, 3, 11, 1, 11);
    private static IconPoint[] One(double x) => Polygon(x, 7, x + 1, 6, x + 2, 6, x + 2, 12, x + 1, 12, x + 1, 7.5);
    private static IconPoint[] Box(double x, double y, double w, double h) => Polygon(x, y, x + w, y, x + w, y + h, x, y + h);

    private static IconPoint[] Polygon(params double[] coordinates)
    {
        var points = new IconPoint[coordinates.Length / 2];
        for (var i = 0; i < points.Length; i++) points[i] = new(coordinates[i * 2], coordinates[i * 2 + 1]);
        return points;
    }
}
