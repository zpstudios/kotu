using KOTU.Module.Document;
using Xunit;
using Xunit.Abstractions;

namespace KOTU.DocumentModel.Tests;

public sealed class EditorDecorLineSearchTests(ITestOutputHelper output)
{
    private const double Epsilon = 0.5;

    [Fact]
    public void EverySmallMonotonePartitionAndHintMatchesIndependentLinearOracle()
    {
        var checkedCases = 0;
        for (var length = 1; length <= 12; length++)
        for (var mask = 0; mask < 1 << (length - 1); mask++)
        {
            var tops = new double[length];
            tops[0] = -32;
            for (var i = 1; i < length; i++)
                tops[i] = tops[i - 1] + ((mask & (1 << (i - 1))) != 0 ? 16 : 0);
            for (var index = 0; index < length; index++)
            for (var hint = 0; hint <= length + 2; hint++)
            {
                Assert.Equal(LinearBoundary(tops, index),
                    EditorDecorLineSearch.FindNext(index, tops[index], length, Epsilon, hint, i => tops[i]));
                checkedCases++;
            }
        }
        Assert.Equal(638976, checkedCases);
    }

    [Fact]
    public void FractionalTopsNegativeOriginsAndVariableAdvancesMatchLinearOracle()
    {
        var random = new Random(401);
        double[] advances = [0, 0.125, 0.25, 0.5, 1, 16, 24];
        for (var sample = 0; sample < 200; sample++)
        {
            var tops = new double[random.Next(2, 2048)];
            tops[0] = -random.Next(1, 10000);
            for (var i = 1; i < tops.Length; i++)
                tops[i] = tops[i - 1] + advances[random.Next(advances.Length)];
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var index = random.Next(tops.Length);
                var hint = random.Next(1, tops.Length + 1);
                Assert.Equal(LinearBoundary(tops, index),
                    EditorDecorLineSearch.FindNext(index, tops[index], tops.Length, Epsilon, hint, i => tops[i]));
            }
        }
    }

    [Fact]
    public void ExactEpsilonIsStillTheCurrentRow()
    {
        double[] tops = [-10, -9.75, -9.5, -9.499, 16];
        for (var hint = 0; hint <= tops.Length; hint++)
            Assert.Equal(3, EditorDecorLineSearch.FindNext(0, tops[0], tops.Length, Epsilon, hint, i => tops[i]));
    }

    [Fact]
    public void ExactHintMustValidateBothSidesOfTheBoundary()
    {
        double[] tops = [0, 0, 0, 16, 16, 32];
        var queries = new List<int>();
        var result = EditorDecorLineSearch.FindNext(0, 0, tops.Length, Epsilon, 3, i =>
        {
            queries.Add(i);
            return tops[i];
        });
        Assert.Equal(3, result);
        Assert.Equal(new[] { 3, 2 }, queries);
        // 후보도 직전 문자도 다음 줄이면 예측을 채택할 수 없다.
        Assert.Equal(3, EditorDecorLineSearch.FindNext(0, 0, tops.Length, Epsilon, 4, i => tops[i]));
        // 후보가 아직 현재 줄이면 실제 경계까지 기존 탐색이 이어진다.
        Assert.Equal(3, EditorDecorLineSearch.FindNext(0, 0, tops.Length, Epsilon, 1, i => tops[i]));
    }

    [Fact]
    public void TerminalAndInvalidHintsNeverQueryEndCaretOrOverflow()
    {
        double[] tops = [0, 0, 16, 16, 32];
        foreach (var hint in new[] { int.MinValue, -1, 0, 1, 2, tops.Length, int.MaxValue })
        for (var index = 0; index < tops.Length; index++)
        {
            Assert.Equal(LinearBoundary(tops, index),
                EditorDecorLineSearch.FindNext(index, tops[index], tops.Length, Epsilon, hint, i =>
                {
                    Assert.InRange(i, 0, tops.Length - 1);
                    return tops[i];
                }));
        }
        Assert.Equal(-1, EditorDecorLineSearch.FindNext(0, 0, 0, Epsilon, 1,
            _ => throw new InvalidOperationException("An empty document has no character rect.")));
        Assert.Equal(-1, EditorDecorLineSearch.FindNext(0, 0, 1, Epsilon, 1,
            _ => throw new InvalidOperationException("The last character has no following character.")));

        const int length = 1_048_576;
        const int start = length - 64;
        var queries = new List<int>();
        Assert.Equal(start + 32,
            EditorDecorLineSearch.FindNext(start, -16, length, Epsilon, int.MaxValue, i =>
            {
                Assert.InRange(i, 0, length - 1);
                queries.Add(i);
                return i < start + 32 ? -16 : 0;
            }));
        Assert.NotEmpty(queries);
    }

    [Fact]
    public void NoNextCharacterRowKeepsTheExistingEofContract()
    {
        double[] tops = [0, 0, 16, 16];
        Assert.Equal(-1, EditorDecorLineSearch.FindNext(2, 16, tops.Length, Epsilon, 2, i => tops[i]));
        Assert.Equal(-1, EditorDecorLineSearch.FindNext(2, 16, tops.Length, Epsilon, 1, i => tops[i]));
        // len 위치의 빈 마지막 줄은 검색기가 아닌 기존 DrawEnd가 담당한다.
        Assert.Equal(-1, EditorDecorLineSearch.FindNext(3, 16, tops.Length, Epsilon, 1,
            _ => throw new InvalidOperationException("Do not query the end caret.")));
    }

    [Fact]
    public void HintRequiresTwoEqualPositiveSpansAndResetsWithEachPass()
    {
        var hint = new EditorDecorLineHint();
        Assert.Equal(0, hint.Span);
        hint.Observe(80);
        Assert.Equal(0, hint.Span);
        hint.Observe(80);
        Assert.Equal(80, hint.Span);
        hint.Observe(120);
        Assert.Equal(0, hint.Span);
        hint.Observe(120);
        Assert.Equal(120, hint.Span);
        hint = default;
        Assert.Equal(0, hint.Span);
        hint.Observe(120);
        Assert.Equal(0, hint.Span);
        hint.Observe(120);
        Assert.Equal(120, hint.Span);
        hint.Observe(0);
        Assert.Equal(0, hint.Span);
        hint.Observe(120);
        Assert.Equal(0, hint.Span);
        hint.Observe(120);
        hint.Observe(-1);
        Assert.Equal(0, hint.Span);
    }

    [Fact]
    public void AlternatingSpansNeverEnableTheHint()
    {
        var hint = new EditorDecorLineHint();
        for (var i = 0; i < 100; i++)
        {
            hint.Observe(i % 2 == 0 ? 1 : 16000);
            Assert.Equal(0, hint.Span);
        }
    }

    [Fact]
    public void FailedHintAddsAtMostTwoUniqueProbesToTheUnchangedFallback()
    {
        for (var length = 2; length <= 128; length++)
        for (var boundary = 1; boundary < length; boundary += 7)
        for (var hint = 1; hint <= length + 1; hint += 3)
        {
            var baseline = new HashSet<int>();
            var predicted = new HashSet<int>();
            var withoutHint = EditorDecorLineSearch.FindNext(0, 0, length, Epsilon, 0, i =>
            {
                baseline.Add(i);
                return i < boundary ? 0 : 16;
            });
            var withHint = EditorDecorLineSearch.FindNext(0, 0, length, Epsilon, hint, i =>
            {
                predicted.Add(i);
                return i < boundary ? 0 : 16;
            });
            Assert.Equal(boundary, withoutHint);
            Assert.Equal(boundary, withHint);
            Assert.True(predicted.Count <= baseline.Count + 2);
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void RepeatedVisualSpansReduceCurrentPassCacheMisses(int span)
    {
        var spans = Enumerable.Repeat(span, 200).ToArray();
        var baseline = WalkRows(spans, useHint: false);
        var predicted = WalkRows(spans, useHint: true);
        Assert.Equal(baseline.Starts, predicted.Starts);
        Assert.True(predicted.Misses < baseline.Misses * 0.4);
        output.WriteLine($"Span {span}, 45 visual rows: {baseline.Misses} -> {predicted.Misses} unique probes.");
    }

    [Fact]
    public void ShortAlternatingAndIrregularRowsDoNotIncreaseModelWork()
    {
        uint seed = 401;
        var irregular = Enumerable.Range(0, 1000).Select(_ =>
        {
            seed = unchecked(seed * 1664525 + 1013904223);
            return 1 + (int)(seed % 160);
        }).ToArray();
        int[][] cases =
        [
            Enumerable.Repeat(2, 1000).ToArray(),
            Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? 1 : 160).ToArray(),
            Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? 1 : 16000).ToArray(),
            irregular,
        ];
        foreach (var spans in cases)
        {
            var baseline = WalkRows(spans, useHint: false);
            var predicted = WalkRows(spans, useHint: true);
            Assert.Equal(baseline.Starts, predicted.Starts);
            Assert.True(predicted.Misses <= baseline.Misses);
            output.WriteLine($"Mixed/short rows: {baseline.Misses} -> {predicted.Misses} unique probes.");
        }
    }

    [Fact]
    public void Utf16ModelKeepsKoreanCombiningSurrogateAndCrlfIndices()
    {
        const string text = "가é😀\r\n나\r\n끝";
        Assert.Equal(11, text.Length);
        // 실제 WinUI 배치가 아닌 UTF-16 좌표 모형이다. CRLF·서로게이트를 임의로 재분할하지 않는다.
        double[] tops = [0, 0, 0, 0, 0, 0, 0, 16, 16, 16, 32];
        for (var index = 0; index < text.Length; index++)
        for (var hint = 0; hint <= text.Length + 1; hint++)
            Assert.Equal(LinearBoundary(tops, index),
                EditorDecorLineSearch.FindNext(index, tops[index], text.Length, Epsilon, hint, i => tops[i]));
    }

    private static int LinearBoundary(double[] tops, int index)
    {
        for (var i = index + 1; i < tops.Length; i++)
            if (tops[i] > tops[index] + Epsilon) return i;
        return -1;
    }

    private static (int Misses, int[] Starts) WalkRows(int[] spans, bool useHint)
    {
        var starts = new int[spans.Length + 1];
        for (var i = 0; i < spans.Length; i++) starts[i + 1] = starts[i] + spans[i];
        var length = starts[^1];
        var cache = new Dictionary<int, double>();
        double TopOf(int index)
        {
            Assert.InRange(index, 0, length - 1);
            if (cache.TryGetValue(index, out var top)) return top;
            var row = Array.BinarySearch(starts, index);
            if (row < 0) row = ~row - 1;
            top = 16 + row * 16;
            cache.Add(index, top);
            return top;
        }
        // 첫 표시 탐색의 기존 조회도 같은 캐시에 들어간다는 조건으로 비교한다.
        var low = 0;
        var high = length - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (TopOf(mid) + 24 > 0) high = mid - 1;
            else low = mid + 1;
        }
        var rowStarts = new List<int>();
        var hint = new EditorDecorLineHint();
        var index = low;
        for (var row = 0; row < 45; row++)
        {
            rowStarts.Add(index);
            var next = EditorDecorLineSearch.FindNext(index, TopOf(index), length, Epsilon,
                useHint ? hint.Span : 0, TopOf);
            if (next < 0) break;
            hint.Observe(next - index);
            index = next;
        }
        return (cache.Count, rowStarts.ToArray());
    }
}

