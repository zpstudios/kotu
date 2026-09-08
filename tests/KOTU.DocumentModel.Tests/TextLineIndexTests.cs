using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class TextLineIndexTests
{
    [Theory]
    [InlineData("", 0, 1, 1)]
    [InlineData("abc", 3, 1, 1)]
    [InlineData("a\nb", 1, 1, 2)]
    [InlineData("a\nb", 2, 2, 2)]
    [InlineData("a\r\nb", 2, 1, 2)]
    [InlineData("a\r\nb", 3, 2, 2)]
    [InlineData("a\r", 2, 2, 2)]
    [InlineData("a\r\n", 3, 2, 2)]
    [InlineData("a\nb\rc\r\n", 7, 4, 4)]
    [InlineData("한글😀\n끝", 5, 2, 2)]
    [InlineData("a\nb", -1, 1, 2)]
    [InlineData("a\nb", 99, 2, 2)]
    public void CaretUsesLogicalLineBoundaries(string text, int caret, int current, int total)
    {
        var index = new TextLineIndex();
        index.Update(text);
        Assert.Equal(total, index.Count);
        Assert.Equal(current, index.GetLineIndex(caret) + 1);
    }

    [Fact]
    public void SharedSnapshotIsReusedAndReplacementRebuilds()
    {
        var index = new TextLineIndex();
        var original = new string(['a', '\r', '\n', 'b']);
        Assert.True(index.Update(original));
        Assert.False(index.Update(original));
        Assert.Equal(3, index.GetStart(1));
        Assert.True(index.Update("single line"));
        Assert.Equal(1, index.Count);
        Assert.Equal(0, index.GetLineIndex(3));
        Assert.True(index.Update(original));
        Assert.Equal(2, index.Count);
        Assert.False(index.Update(original));
    }
}
