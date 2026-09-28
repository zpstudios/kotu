namespace KOTU.Module.Document;

/// <summary>기존 블록 상한과 표 셀 상한을 함께 적용하되, 큰 한 행도 반드시 진행한다.</summary>
internal static class MarkdownRenderBatch
{
    private const int MaxTableCells = 180;

    /// <summary>동기 인쇄 측정 예산: 일반 블록은 1, 표는 셀마다 1을 소비한다.</summary>
    public static bool FitsPrintBudget(IReadOnlyList<MdBlock> blocks, int budget)
    {
        if (budget < 0) return false;
        var remaining = budget;
        foreach (var block in blocks)
        {
            var cost = Math.Max(1, block.Table?.Cells.Count ?? 1);
            // 합산 전에 남은 예산과 비교해 오버플로와 불필요한 나머지 순회를 피한다.
            if (cost > remaining) return false;
            remaining -= cost;
        }
        return true;
    }

    public static int Count(IReadOnlyList<MdBlock> blocks, int start, int maxBlocks)
    {
        var count = 0;
        var cells = 0;
        while (start + count < blocks.Count && count < maxBlocks)
        {
            var nextCells = blocks[start + count].Table?.Cells.Count ?? 0;
            if (count > 0 && cells + nextCells > MaxTableCells) break;
            cells += nextCells;
            count++;
        }
        return count;
    }
}
