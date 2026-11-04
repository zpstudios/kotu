namespace KOTU.Ui;

internal enum ToolbarIconKind { Play, Pause, Previous, Next, Sound, Muted, OriginalRatio }
internal readonly record struct IconPoint(double X, double Y);

/// <summary>하단 바 도형의 단일 좌표 원본. 종류별 고정 뷰포트 안의 채움 윤곽이며 UI·글꼴에 의존하지 않는다.</summary>
internal static class ToolbarIconGeometry
{
    internal const double Size = 18;
    internal static double ViewportSize(ToolbarIconKind kind) => kind == ToolbarIconKind.OriginalRatio ? 24 : Size;

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
            // 바깥과 안쪽 윤곽은 짝홀 채움으로 테두리를 만든다. 메뉴·버튼 모두 같은 도형이다.
            OriginalRoundedBox(1, 3, 22, 18, 2),
            OriginalRoundedBox(2, 4, 20, 16, 1),
            One(5), One(15), OriginalRoundedBox(11, 8, 2, 2, 1), OriginalRoundedBox(11, 14, 2, 2, 1),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

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
        points.AddRange(right.Reverse());
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
    private static IconPoint[] One(double x)
    {
        var vertices = Polygon(x, 9, x + 2, 7, x + 4, 7, x + 4, 17, x + 2, 17, x + 2, 9);
        var points = new List<IconPoint>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var corner = vertices[i];
            // 깃 끝과 오목한 어깨는 그대로 두어 숫자의 폭과 위치를 보존한다.
            if (i is 0 or 5)
            {
                points.Add(corner);
                continue;
            }

            var start = TrimToward(corner, vertices[i - 1]);
            var end = TrimToward(corner, vertices[i + 1]);
            points.Add(start);
            for (var step = 1; step <= 6; step++)
            {
                var t = step / 6.0;
                var u = 1 - t;
                points.Add(step == 6 ? end : new(
                    u * u * start.X + 2 * u * t * corner.X + t * t * end.X,
                    u * u * start.Y + 2 * u * t * corner.Y + t * t * end.Y));
            }
        }
        return points.ToArray();

        static IconPoint TrimToward(IconPoint corner, IconPoint neighbor)
        {
            var dx = neighbor.X - corner.X;
            var dy = neighbor.Y - corner.Y;
            var ratio = 0.35 / Math.Sqrt(dx * dx + dy * dy);
            return new(corner.X + dx * ratio, corner.Y + dy * ratio);
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
