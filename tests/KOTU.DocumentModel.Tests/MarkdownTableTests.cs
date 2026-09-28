using KOTU.Module.Document;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class MarkdownTableTests
{
    [Fact]
    public void AlignmentsAndInlineFormattingAreSharedByHeaderAndBody()
    {
        var blocks = MarkdownParser.Parse("| **Name** | *Code* | Link |\n| :--- | :---: | ---: |\n| value | `x` | [site](https://example.com) |");
        Assert.Equal(2, blocks.Count);
        var header = Assert.IsType<MdTableRow>(blocks[0].Table);
        var body = Assert.IsType<MdTableRow>(blocks[1].Table);
        Assert.All(blocks, block => Assert.Equal(MdBlockKind.TableRow, block.Kind));
        Assert.Equal(new[] { MdColumnAlignment.Left, MdColumnAlignment.Center, MdColumnAlignment.Right }, header.Alignments);
        Assert.Same(header.Alignments, body.Alignments);
        Assert.True(header.IsHeader);
        Assert.False(header.IsLast);
        Assert.False(body.IsHeader);
        Assert.True(body.IsLast);
        Assert.True(Assert.Single(header.Cells[0]).Bold);
        Assert.True(Assert.Single(header.Cells[1]).Italic);
        Assert.True(Assert.Single(body.Cells[1]).Code);
        Assert.Equal("https://example.com", Assert.Single(body.Cells[2]).LinkUrl);
    }

    [Fact]
    public void ShortRowsArePaddedAndExtraCellsAreTruncated()
    {
        var blocks = MarkdownParser.Parse("A | B | C\n--- | --- | ---\nx | y\n1 | 2 | 3 | 4");
        Assert.Equal(3, blocks.Count);
        Assert.Equal(3, blocks[1].Table!.Cells.Count);
        Assert.Empty(blocks[1].Table!.Cells[2]);
        Assert.Equal(new[] { "1", "2", "3" }, blocks[2].Table!.Cells.Select(Text));
    }

    [Theory]
    [InlineData("A | B\n-- | ---")]
    [InlineData("A | B\n--- | --- | ---")]
    [InlineData("A | B\n--- | -x-")]
    [InlineData("A | B\n--- | : :")]
    [InlineData("A\n---")]
    [InlineData("A | B\njust | text")]
    [InlineData("`A|B`\n|---|")]
    public void InvalidDelimiterOrNoActualPipePreservesNonTableBlocks(string text)
    {
        Assert.DoesNotContain(MarkdownParser.Parse(text), block => block.Kind == MdBlockKind.TableRow);
    }

    [Fact]
    public void ParagraphImmediatelyBeforeTableIsNotConsumedAsHeader()
    {
        var blocks = MarkdownParser.Parse("intro\nA | B\n--- | ---\nx | y\n\nafter");
        Assert.Equal(new[] { MdBlockKind.Paragraph, MdBlockKind.TableRow, MdBlockKind.TableRow, MdBlockKind.Paragraph }, blocks.Select(b => b.Kind));
        Assert.Equal("intro", Text(blocks[0].Spans));
        Assert.Equal("after", Text(blocks[3].Spans));
    }

    [Fact]
    public void EscapedPipesAndCodePipesStayInCells()
    {
        var blocks = MarkdownParser.Parse("A | B\n--- | ---\na\\|b | `c|d`\n\\\\ | e\\|f\n`unclosed | ordinary");
        Assert.Equal(new[] { "a|b", "c|d" }, blocks[1].Table!.Cells.Select(Text));
        Assert.True(Assert.Single(blocks[1].Table!.Cells[1]).Code);
        Assert.Equal(new[] { "\\", "e|f" }, blocks[2].Table!.Cells.Select(Text));
        Assert.Equal(new[] { "`unclosed", "ordinary" }, blocks[3].Table!.Cells.Select(Text));
    }

    [Fact]
    public void OptionalOuterPipesAndEmptyCellsArePreserved()
    {
        var blocks = MarkdownParser.Parse("| A | B | C |\n--- | --- | ---\n| | x | |\n|only|");
        Assert.Equal(new[] { "", "x", "" }, blocks[1].Table!.Cells.Select(Text));
        Assert.Equal(new[] { "only", "", "" }, blocks[2].Table!.Cells.Select(Text));
    }

    [Fact]
    public void HeaderOnlySingleColumnTableIsSupported()
    {
        var block = Assert.Single(MarkdownParser.Parse("| Header |\n| :---: |"));
        Assert.True(block.Table!.IsHeader);
        Assert.True(block.Table.IsLast);
        Assert.Equal(MdColumnAlignment.Center, Assert.Single(block.Table.Alignments));
    }

    [Fact]
    public void FencesAndOtherBlockSyntaxKeepTheirExistingMeaning()
    {
        var blocks = MarkdownParser.Parse("```\nA | B\n--- | ---\n```\n# Heading\n- list\n> quote\n---\n**bold** [link](https://example.com)");
        Assert.Equal(new[] { MdBlockKind.CodeBlock, MdBlockKind.Heading, MdBlockKind.ListItem, MdBlockKind.Quote, MdBlockKind.Rule, MdBlockKind.Paragraph }, blocks.Select(b => b.Kind));
        Assert.Equal("A | B\n--- | ---", blocks[0].Literal);
        Assert.True(blocks[^1].Spans[0].Bold);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public void NewlineStylesProduceSameTable(string newline)
    {
        Assert.Equal(2, MarkdownParser.Parse(string.Join(newline, "A|B", "---|---", "x|y")).Count);
    }

    [Fact]
    public void LargeTableRemainsRowGranularForChunkRenderingAndPrintPacking()
    {
        var source = "A|B\n---|---\n" + string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"{i}|value"));
        var blocks = MarkdownParser.Parse(source);
        Assert.Equal(2001, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(2, block.Table!.Cells.Count));
        Assert.All(blocks.Take(2000), block => Assert.False(block.Table!.IsLast));
        Assert.True(blocks[^1].Table!.IsLast);
    }

    private static string Text(IReadOnlyList<MdSpan> spans) => string.Concat(spans.Select(s => s.Text));

    [Fact]
    public void BodyWithoutPipeIsOneCellUntilBlankOrNewBlock()
    {
        var blocks = MarkdownParser.Parse("A|B\n---|---\nonlyone\n`code|pipe`\nescaped\\|pipe\n\nparagraph\n\nA|B\n---|---\nlast\n# Heading");
        Assert.Equal(new[] { "onlyone", "" }, blocks[1].Table!.Cells.Select(Text));
        Assert.Equal(new[] { "code|pipe", "" }, blocks[2].Table!.Cells.Select(Text));
        Assert.True(Assert.Single(blocks[2].Table!.Cells[0]).Code);
        Assert.Equal(new[] { "escaped|pipe", "" }, blocks[3].Table!.Cells.Select(Text));
        Assert.True(blocks[3].Table!.IsLast);
        Assert.Equal(MdBlockKind.Paragraph, blocks[4].Kind);
        Assert.Equal("paragraph", Text(blocks[4].Spans));
        Assert.True(blocks[6].Table!.IsLast);
        Assert.Equal(MdBlockKind.Heading, blocks[7].Kind);
    }

    [Fact]
    public void NonTableChunkRetainsSixtyBlockLimitAndHandlesFinalChunk()
    {
        var blocks = MarkdownParser.Parse(string.Join("\n\n", Enumerable.Repeat("paragraph", 125)));
        Assert.Equal(60, MarkdownRenderBatch.Count(blocks, 0, 60));
        Assert.Equal(60, MarkdownRenderBatch.Count(blocks, 60, 60));
        Assert.Equal(5, MarkdownRenderBatch.Count(blocks, 120, 60));
        Assert.Equal(0, MarkdownRenderBatch.Count(blocks, 125, 60));
    }

    [Fact]
    public void WideTableChunkLimitsCellsAndEventuallyConsumesEveryRow()
    {
        var header = string.Join('|', Enumerable.Repeat("head", 20));
        var delimiter = string.Join('|', Enumerable.Repeat("---", 20));
        var blocks = MarkdownParser.Parse(header + "\n" + delimiter + "\n" + string.Join('\n', Enumerable.Repeat(header, 99)));
        Assert.Equal(9, MarkdownRenderBatch.Count(blocks, 0, 60));
        var processed = 0;
        while (processed < blocks.Count)
        {
            var count = MarkdownRenderBatch.Count(blocks, processed, 60);
            Assert.InRange(count, 1, 9);
            processed += count;
        }
        Assert.Equal(100, processed);
    }

    [Fact]
    public void SingleOversizedRowCannotStallRendering()
    {
        var header = string.Join('|', Enumerable.Repeat("head", 181));
        var delimiter = string.Join('|', Enumerable.Repeat("---", 181));
        var blocks = MarkdownParser.Parse(header + "\n" + delimiter + "\n" + header);
        Assert.Equal(1, MarkdownRenderBatch.Count(blocks, 0, 60));
        Assert.Equal(1, MarkdownRenderBatch.Count(blocks, 1, 60));
        Assert.Equal(181, blocks[0].Table!.Cells.Count);
    }
}
