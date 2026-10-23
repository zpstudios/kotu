namespace KOTU.Module.Document;

/// <summary>시각적 줄의 경계를 찾는다. 힌트도 현재 패스의 실제 좌표로 검증한다.</summary>
internal static class EditorDecorLineSearch
{
    internal static int FindNext(int index, double referenceTop, int length, double epsilon,
        int hint, Func<int, double> topOf)
    {
        if (index >= length - 1) return -1;
        var threshold = referenceTop + epsilon;
        // 끝 캐럿은 조회하지 않는다. 길이 차부터 비교해 후보 덧셈의 넘침도 막는다.
        if (hint > 0 && hint < length - index)
        {
            var candidate = index + hint;
            // Y가 단조라는 기존 탐색 계약에서 두 이웃이 경계를 증명할 때만 채택한다.
            if (topOf(candidate) > threshold && topOf(candidate - 1) <= threshold)
                return candidate;
        }

        // 힌트 실패 시 기존 지수·이진 탐색을 그대로 쓴다. 조회한 좌표는 호출자의 캐시에 남는다.
        var low = index;
        var high = -1;
        var step = 32;
        while (high < 0)
        {
            var probe = Math.Min(length - 1, low + step);
            if (topOf(probe) > threshold)
            {
                high = probe;
                break;
            }
            if (probe == length - 1) return -1;
            low = probe;
            step *= 2;
        }
        while (low + 1 < high)
        {
            var mid = low + (high - low) / 2;
            if (topOf(mid) > threshold) high = mid;
            else low = mid;
        }
        return high;
    }
}

/// <summary>렌더 패스의 지역 값. 직전 두 줄의 양수 문자 길이가 같을 때만 힌트를 낸다.</summary>
internal struct EditorDecorLineHint
{
    private int _previousSpan;
    private int _lastSpan;

    internal readonly int Span => _lastSpan > 0 && _lastSpan == _previousSpan ? _lastSpan : 0;

    internal void Observe(int span)
    {
        if (span <= 0)
        {
            this = default;
            return;
        }
        _previousSpan = _lastSpan;
        _lastSpan = span;
    }
}
