namespace KOTU.Module.Document;

/// <summary>기존 블록 상한과 표 셀 상한을 함께 적용하되, 큰 한 행도 반드시 진행한다.</summary>
internal static class MarkdownRenderBatch
{
    private const int MaxTableCells = 180;

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
