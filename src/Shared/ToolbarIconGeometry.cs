namespace KOTU.Ui;

internal enum ToolbarIconKind { Play, Pause, Previous, Next, Sound, Muted, OriginalRatio, FitContain, FitWidth, FitHeight }
internal readonly record struct IconPoint(double X, double Y);

/// <summary>하단 바 도형의 단일 좌표 원본. 종류별 고정 뷰포트 안의 채움 윤곽이며 UI·글꼴에 의존하지 않는다.</summary>
internal static class ToolbarIconGeometry
{
    internal const double Size = 18;
    internal static double ViewportSize(ToolbarIconKind kind) => kind is ToolbarIconKind.OriginalRatio
        or ToolbarIconKind.FitContain or ToolbarIconKind.FitWidth or ToolbarIconKind.FitHeight ? 24 : Size;

    internal static IconPoint[][] Create(ToolbarIconKind kind, bool thin = false)
    {
        var radius = thin ? 0.5 : 0.7;
        var dot = radius + 0.1;
        // Fit, playback and volume are drawn from centerlines, so the same weight
        // changes the whole family without moving its optical centers or hit targets.
        IconPoint[][] Line(params double[] points) => Stroke(radius, points);
        IconPoint[][] Speaker() => Line(1.4, 7, 4, 7, 7.5, 3.4, 7.5, 14.6, 4, 11, 1.4, 11, 1.4, 7);
        IconPoint[][] Previous() => [
            .. Line(3, 2 + radius, 3, 16 - radius),
            .. Line(14.8, 2 + radius, 5.4, 9, 14.8, 16 - radius, 14.8, 2 + radius),
        ];
        IconPoint[][] FitWidth() => [
            .. Line(4, 6, 4, 18), .. Line(20, 6, 20, 18), .. Line(7, 12, 17, 12),
            .. Line(10, 9, 7, 12, 10, 15), .. Line(14, 9, 17, 12, 14, 15),
        ];
        return kind switch
        {
            ToolbarIconKind.Play => Line(5.4, 2 + radius, 15.6, 9, 5.4, 16 - radius, 5.4, 2 + radius),
            ToolbarIconKind.Pause => [
                .. Line(6, 2 + radius, 6, 16 - radius), .. Line(12, 2 + radius, 12, 16 - radius),
            ],
            ToolbarIconKind.Previous => Previous(),
            // Reflection reverses winding; reverse traversal to keep nonzero fills consistent.
            ToolbarIconKind.Next => Previous()
                .Select(c => Enumerable.Reverse(c).Select(p => new IconPoint(Size - p.X, p.Y)).ToArray())
                .ToArray(),
            ToolbarIconKind.Sound => [
                .. Speaker(),
                SoundWave(new(10, 6), new(12.6, 7.4), new(12.6, 10.6), new(10, 12), radius),
                SoundWave(new(12.5, 3.5), new(17.2, 6), new(17.2, 12), new(12.5, 14.5), radius),
            ],
            ToolbarIconKind.Muted => [ .. Speaker(), .. Line(10.1, 6, 16.1, 12), .. Line(16.1, 6, 10.1, 12) ],
            ToolbarIconKind.OriginalRatio => [
                .. Line(4.5, 8, 7, 6, 7, 18), .. Line(4.5, 18, 9.5, 18),
                .. Line(14.5, 8, 17, 6, 17, 18), .. Line(14.5, 18, 19.5, 18),
                OriginalRoundedBox(12 - dot, 10 - dot, 2 * dot, 2 * dot, dot),
                OriginalRoundedBox(12 - dot, 15 - dot, 2 * dot, 2 * dot, dot),
            ],
            ToolbarIconKind.FitContain => [
                .. Line(7, 4, 4, 4, 4, 7), .. Line(17, 4, 20, 4, 20, 7),
                .. Line(20, 17, 20, 20, 17, 20), .. Line(7, 20, 4, 20, 4, 17),
                .. Line(9, 8, 15, 8, 15, 16, 9, 16, 9, 8),
            ],
            ToolbarIconKind.FitWidth => FitWidth(),
            ToolbarIconKind.FitHeight => FitWidth()
                .Select(c => c.Select(p => new IconPoint(24 - p.Y, p.X)).ToArray())
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    // PathIcon은 Stroke를 지원하지 않아 둥근 선을 같은 방향의 채움 윤곽으로 만든다.
    // Nonzero 채움으로 겹쳐 화살표 접합부가 EvenOdd의 빈 구멍으로 잘리지 않게 한다.
    private static IconPoint[][] Stroke(double radius, double[] coordinates)
    {
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

    // 스피커의 곡선 중심선을 선택한 획 굵기의 채움 윤곽으로 편다.
    // 원본 배율 상자처럼 곡선을 세분화한다. 32구간·반원 16구간으로 200%에서도 각이 드러나지 않는다.
    private static IconPoint[] SoundWave(IconPoint start, IconPoint control1, IconPoint control2, IconPoint end, double radius)
    {
        const int steps = 32;

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
        points.Reverse();
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

    private static IconPoint[] Polygon(params double[] coordinates)
    {
        var points = new IconPoint[coordinates.Length / 2];
        for (var i = 0; i < points.Length; i++) points[i] = new(coordinates[i * 2], coordinates[i * 2 + 1]);
        return points;
    }
}
