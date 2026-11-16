namespace KOTU.Ui;

internal enum ToolbarIconKind { Play, Pause, Previous, Next, Sound, Muted, OriginalRatio, FitContain, FitWidth, FitHeight }
internal readonly record struct IconPoint(double X, double Y);

/// <summary>하단 바 도형의 단일 좌표 원본. 종류별 고정 뷰포트 안의 채움 윤곽이며 UI·글꼴에 의존하지 않는다.</summary>
internal static class ToolbarIconGeometry
{
    internal const double Size = 18;
    internal static bool UsesStrokeContours(ToolbarIconKind kind) => kind is
        ToolbarIconKind.OriginalRatio or ToolbarIconKind.FitContain or ToolbarIconKind.FitWidth or ToolbarIconKind.FitHeight;
    internal static double ViewportSize(ToolbarIconKind kind) => UsesStrokeContours(kind) ? 24 : Size;

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
            SoundWave(new(10, 6), new(12.6, 7.4), new(12.6, 10.6), new(10, 12)),
            SoundWave(new(12.5, 3.5), new(17.2, 6), new(17.2, 12), new(12.5, 14.5)),
        ],
        ToolbarIconKind.Muted =>
        [
            Speaker(),
            Polygon(10, 5, 13, 8, 16, 5, 17, 6, 14, 9, 17, 12, 16, 13, 13, 10, 10, 13, 9, 12, 12, 9, 9, 6),
        ],
        ToolbarIconKind.OriginalRatio =>
        [
            .. Stroke(4.5, 8, 7, 6, 7, 18), .. Stroke(4.5, 18, 9.5, 18),
            .. Stroke(14.5, 8, 17, 6, 17, 18), .. Stroke(14.5, 18, 19.5, 18),
            OriginalRoundedBox(11.2, 9.2, 1.6, 1.6, 0.8), OriginalRoundedBox(11.2, 14.2, 1.6, 1.6, 0.8),
        ],
        // 원본 비율·화면 여백·가로·세로를 서로 다른 기호로 표시한다. 24px 좌표와 1.4px 둥근 선을 공유한다.
        ToolbarIconKind.FitContain =>
        [
            .. Stroke(7, 4, 4, 4, 4, 7), .. Stroke(17, 4, 20, 4, 20, 7),
            .. Stroke(20, 17, 20, 20, 17, 20), .. Stroke(7, 20, 4, 20, 4, 17),
            .. Stroke(9, 8, 15, 8, 15, 16, 9, 16, 9, 8),
        ],
        ToolbarIconKind.FitWidth => FitWidth(),
        ToolbarIconKind.FitHeight => FitWidth()
            .Select(contour => contour.Select(p => new IconPoint(24 - p.Y, p.X)).ToArray()).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static IconPoint[][] FitWidth() =>
    [
        .. Stroke(4, 6, 4, 18), .. Stroke(20, 6, 20, 18),
        .. Stroke(7, 12, 17, 12),
        .. Stroke(10, 9, 7, 12, 10, 15), .. Stroke(14, 9, 17, 12, 14, 15),
    ];

    // PathIcon은 Stroke를 지원하지 않아 둥근 선을 같은 방향의 채움 윤곽으로 만든다.
    // Nonzero 채움으로 겹쳐 화살표 접합부가 EvenOdd의 빈 구멍으로 잘리지 않게 한다.
    private static IconPoint[][] Stroke(params double[] coordinates)
    {
        const double radius = 0.7;
        const int capSteps = 12;
        var vertices = Polygon(coordinates);
        return vertices.Zip(vertices.Skip(1), (start, end) =>
        {
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            var outline = new List<IconPoint>();
            AddCap(start, angle + Math.PI / 2);
            AddCap(end, angle - Math.PI / 2);
            return outline.ToArray();

            void AddCap(IconPoint center, double from)
            {
                for (var i = 0; i <= capSteps; i++)
                {
                    var a = from + Math.PI * i / capSteps;
                    outline.Add(new(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a)));
                }
            }
        }).ToArray();
    }

    private static IconPoint[] Speaker() => Polygon(1, 7, 3, 7, 7, 3, 7, 15, 3, 11, 1, 11);

    // A415 승인 A안의 cubic 중심선 + 1.35 round stroke를 기존 PathIcon용 채움 윤곽으로 편다.
    // 원본 배율 상자처럼 곡선을 세분화한다. 32구간·반원 16구간으로 200%에서도 각이 드러나지 않는다.
    private static IconPoint[] SoundWave(IconPoint start, IconPoint control1, IconPoint control2, IconPoint end)
    {
        const int steps = 32;
        const double radius = 1.35 / 2;
        var left = new IconPoint[steps + 1];
        var right = new IconPoint[steps + 1];
        double startNormal = 0, endNormal = 0;
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var u = 1 - t;
            var x = u * u * u * start.X + 3 * u * u * t * control1.X + 3 * u * t * t * control2.X + t * t * t * end.X;
            var y = u * u * u * start.Y + 3 * u * u * t * control1.Y + 3 * u * t * t * control2.Y + t * t * t * end.Y;
            var dx = u * u * (control1.X - start.X) + 2 * u * t * (control2.X - control1.X) + t * t * (end.X - control2.X);
            var dy = u * u * (control1.Y - start.Y) + 2 * u * t * (control2.Y - control1.Y) + t * t * (end.Y - control2.Y);
            var scale = radius / Math.Sqrt(dx * dx + dy * dy);
            left[i] = new(x - dy * scale, y + dx * scale);
            right[i] = new(x + dy * scale, y - dx * scale);
            if (i == 0) startNormal = Math.Atan2(dx, -dy);
            if (i == steps) endNormal = Math.Atan2(dx, -dy);
        }
        var points = new List<IconPoint>(left);
        AddCap(end, endNormal);
        points.AddRange(Enumerable.Reverse(right));
        AddCap(start, startNormal + Math.PI);
        return points.ToArray();

        void AddCap(IconPoint center, double normal)
        {
            for (var i = 1; i < 16; i++)
            {
                var angle = normal - i * Math.PI / 16;
                points.Add(new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
            }
        }
    }
    private static IconPoint[] OriginalRoundedBox(double x, double y, double w, double h, double radius)
    {
        var centers = Polygon(x + w - radius, y + radius, x + w - radius, y + h - radius,
            x + radius, y + h - radius, x + radius, y + radius);
        var starts = Polygon(x + w - radius, y, x + w, y + h - radius,
            x + radius, y + h, x, y + radius);
        var ends = Polygon(x + w, y + radius, x + w - radius, y + h,
            x, y + h - radius, x + radius, y);
        var points = new List<IconPoint>();
        for (var corner = 0; corner < 4; corner++)
        {
            for (var step = 0; step <= 8; step++)
            {
                var angle = (corner - 1 + step / 8.0) * Math.PI / 2;
                // 접점은 삼각함수 오차 없이 보존하고 원형 콜론의 중복 접점은 생략한다.
                var point = step == 0 ? starts[corner] : step == 8 ? ends[corner] : new IconPoint(
                    centers[corner].X + radius * Math.Cos(angle),
                    centers[corner].Y + radius * Math.Sin(angle));
                if (points.Count == 0 || points[^1] != point) points.Add(point);
            }
        }
        if (points[^1] == points[0]) points.RemoveAt(points.Count - 1);
        return points.ToArray();
    }
    private static IconPoint[] Box(double x, double y, double w, double h) => Polygon(x, y, x + w, y, x + w, y + h, x, y + h);

    private static IconPoint[] Polygon(params double[] coordinates)
    {
        var points = new IconPoint[coordinates.Length / 2];
        for (var i = 0; i < points.Length; i++) points[i] = new(coordinates[i * 2], coordinates[i * 2 + 1]);
        return points;
    }
}
