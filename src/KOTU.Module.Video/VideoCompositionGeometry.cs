using Windows.Foundation;

namespace KOTU.Module.Video;

internal static class VideoCompositionGeometry
{
    internal const double BasisLength = 1024;
    private const double MatchTolerance = 1e-5;

    internal static bool TryGetTargetScale(Point origin, Point basisX, Point basisY, double rasterization,
        out Size scale)
    {
        scale = default;
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) ||
            !double.IsFinite(basisX.X) || !double.IsFinite(basisX.Y) ||
            !double.IsFinite(basisY.X) || !double.IsFinite(basisY.Y) ||
            !double.IsFinite(rasterization) || rasterization <= 0)
            return false;
        var x = (basisX.X - origin.X) / BasisLength;
        var y = (basisY.Y - origin.Y) / BasisLength;
        // 축 교차 성분이 있는 회전·기울임은 지원하지 않는다. 양의 축 정렬 배율만 허용한다.
        if (basisX.Y != origin.Y || basisY.X != origin.X || x <= 0 || y <= 0) return false;
        scale = new Size(x * rasterization, y * rasterization);
        return IsPositiveFinite(scale) &&
            HasReliablePrecision(origin.X, basisX.X, rasterization, scale.Width) &&
            HasReliablePrecision(origin.Y, basisY.Y, rasterization, scale.Height);
    }

    internal static bool IsPositiveFinite(Size scale) =>
        double.IsFinite(scale.Width) && double.IsFinite(scale.Height) && scale.Width > 0 && scale.Height > 0;

    internal static bool ScalesMatch(Size target, Size actual) =>
        IsPositiveFinite(target) && IsPositiveFinite(actual) &&
        AxisMatches(target.Width, actual.Width) && AxisMatches(target.Height, actual.Height);

    private static bool AxisMatches(double target, double actual) =>
        Math.Abs(target - actual) <= MatchTolerance * Math.Max(1, Math.Max(Math.Abs(target), Math.Abs(actual)));

    private static bool HasReliablePrecision(double origin, double endpoint, double rasterization, double scale)
    {
        // 반올림된 Point 좌표 기준으로 절댓값의 2^-23과 최소 subnormal을 보수적 오차 상한으로 쓴다.
        // 뺄셈 양쪽 오차를 더하고 1024로 나눈 뒤, 배율 허용 오차의 1/4까지만 쓴다.
        // 큰 평행이동으로 이 상한을 넘으면 영구 대기 대신 정상 배치를 선택한다.
        const double unitRoundoff = 1d / (1 << 23);
        var uncertainty = (Math.Abs(origin) * unitRoundoff + Math.Abs(endpoint) * unitRoundoff +
            2 * float.Epsilon) / BasisLength * rasterization;
        return uncertainty <= MatchTolerance * Math.Max(1, scale) / 4;
    }
}
