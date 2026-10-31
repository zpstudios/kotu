using System.Text.Json;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class VirtualDocumentBufferTests
{
    [Fact]
    public void BrowserWireFramesPreserveSurrogatePairAcrossRealJsonBoundary()
    {
        using var resource = typeof(VirtualDocumentBufferTests).Assembly.GetManifestResourceStream("EditorSurrogateWire")!;
        using var gzip = new System.IO.Compression.GZipStream(resource, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var assembler = new EditorMessageAssembler("test");
        string? result = null;
        int count = 0;
        while (reader.ReadLine() is { } line)
        {
            using var frame = JsonDocument.Parse(line);
            var root = frame.RootElement;
            result = assembler.Accept(root.GetProperty("epoch").GetString()!, root.GetProperty("transfer").GetInt64(),
                root.GetProperty("index").GetInt32(), root.GetProperty("total").GetInt32(), root.GetProperty("data").GetString()!);
            count++;
        }
        Assert.Equal(2, count);
        using var message = JsonDocument.Parse(result!);
        Assert.EndsWith("😀end", message.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void StaleCloseCancellationCannotReleaseNewFreezeInSamePane()
    {
        var freeze = new EditorFreezeOwnership();
        freeze.Acquire(1);
        Assert.True(freeze.Release(1));
        freeze.Acquire(2);
        Assert.False(freeze.Release(1));
        Assert.True(freeze.Owns(2));
        Assert.True(freeze.Release(2));
    }
    [Theory]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\nb\rc\nd", "a\nb\nc\nd")]
    public void NormalizesAllNewlineForms(string input, string expected)
    {
        var buffer = new VirtualDocumentBuffer(input);
        Assert.Equal(expected, buffer.Text);
        Assert.Equal(expected.Count(c => c == '\n') + 1, buffer.LineCount);
        Assert.Same(buffer.Text, buffer.Text);
    }

    [Fact]
    public void DeltasUseOriginalUtf16OffsetsAndCachedRevision()
    {
        var buffer = new VirtualDocumentBuffer("a😀b\nc");
        var before = buffer.Text;
        Assert.True(buffer.Apply(buffer.Epoch, 2, [new(6, 0, "!"), new(1, 2, "한\n글")]));
        Assert.Equal("a한\n글b\nc!", buffer.Text);
        Assert.Equal(3, buffer.LineCount);
        Assert.Equal("a😀b\nc", before);
        Assert.Same(buffer.Text, buffer.Text);
        buffer.SetState(buffer.Epoch, 2, new(2, 5, 2, 3), true);
        Assert.True(buffer.IsComposing);
        Assert.Equal(5, buffer.Selection.Position);
    }

    [Fact]
    public void RejectsEntireBatchWithoutPartiallyApplyingFirstValidDelta()
    {
        foreach (var invalid in new EditorDelta[][]
        {
            [new(5, 0, "valid"), new(-1, 0, "bad")],
            [new(4, 1, "valid"), new(3, 2, "overlap")],
            [new(1, 0, "valid"), new(4, 0, "order")],
            [new(3, 0, "valid"), new(3, 0, "same offset")],
            [new(4, 0, "valid"), new(1, 0, "\r")],
        })
        {
            var buffer = new VirtualDocumentBuffer("abcdef");
            var original = buffer.Text;
            Assert.Throws<InvalidDataException>(() => buffer.Apply(buffer.Epoch, 2, invalid));
            Assert.Same(original, buffer.Text);
            Assert.Equal(1, buffer.Version);
        }
    }

    [Fact]
    public void RejectsStaleVersionEpochAndMismatchedSnapshots()
    {
        var buffer = new VirtualDocumentBuffer("abc");
        Assert.False(buffer.Apply("previous", 2, [new(0, 0, "x")]));
        Assert.Throws<InvalidDataException>(() => buffer.Apply(buffer.Epoch, 3, [new(0, 0, "x")]));
        Assert.Throws<InvalidDataException>(() => buffer.VerifySnapshot(buffer.Epoch, 1, "abcd", false));
        Assert.Throws<InvalidDataException>(() => buffer.VerifySnapshot("previous", 1, "abc", false));
        Assert.Throws<InvalidDataException>(() => buffer.SetState(buffer.Epoch, 1, new(0, 4, 1, 5), false));
        Assert.Equal("abc", buffer.Text);
    }

    [Fact]
    public void ExistingMaximumReplacementAndWorstEscapingSurviveFramedTransport()
    {
        var text = new string('\0', TextSearch.MaxReplacementLength);
        var buffer = new VirtualDocumentBuffer("old");
        buffer.Apply(buffer.Epoch, 2, [new(0, 3, text)]);
        var json = JsonSerializer.Serialize(new { text });
        Assert.True(json.Length > 32 * 1024 * 1024);
        var assembler = new EditorMessageAssembler(buffer.Epoch);
        string? assembled = null;
        for (int offset = 0, index = 0; offset < json.Length; offset += EditorMessageAssembler.FrameChars, index++)
        {
            var part = json.Substring(offset, Math.Min(EditorMessageAssembler.FrameChars, json.Length - offset));
            assembled = assembler.Accept(buffer.Epoch, 1, index, json.Length, part);
        }
        Assert.Equal(json, assembled);
        Assert.Equal(text, buffer.VerifySnapshot(buffer.Epoch, 2, text, false).Text);
        // 기존 치환 상한은 치환 함수의 계약이며 일반 입력의 새 상한이 아니다.
        buffer.Apply(buffer.Epoch, 3, [new(buffer.Length, 0, "x")]);
        Assert.Equal(TextSearch.MaxReplacementLength + 1, buffer.Length);
    }

    [Fact]
    public void TransferRejectsInterleavingOutOfOrderAndWrongEpoch()
    {
        var assembler = new EditorMessageAssembler("epoch");
        var first = new string('x', EditorMessageAssembler.FrameChars);
        Assert.Null(assembler.Accept("old", 1, 0, first.Length + 1, first));
        Assert.Null(assembler.Accept("epoch", 1, 0, first.Length + 1, first));
        Assert.Throws<InvalidDataException>(() => assembler.Accept("epoch", 2, 0, first.Length + 1, first));
        Assert.Throws<InvalidDataException>(() => assembler.Accept("epoch", 1, 2, first.Length + 1, "x"));
        Assert.Equal(first + "y", assembler.Accept("epoch", 1, 1, first.Length + 1, "y"));
    }

    [Fact]
    public async Task RequestLifetimeIgnoresWrongEpochLateResponsesAndDisposal()
    {
        using var requests = new EditorRequestRegistry("epoch");
        var abandoned = requests.Begin("freeze", 2);
        Assert.False(requests.Complete("old", abandoned.Id, 2, null));
        requests.Abandon(abandoned);
        Assert.False(requests.Complete("epoch", abandoned.Id, 2, null));
        var stale = requests.Begin("snapshot", 3);
        requests.Complete("epoch", stale.Id, 2, null);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await stale.Completion.Task);
        var pending = requests.Begin("snapshot", 3);
        requests.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.Completion.Task);
        Assert.False(requests.Complete("epoch", pending.Id, 3, null));
        Assert.Equal(0, requests.PendingCount);
    }

    [Fact]
    public void SaveCommitsOnlyAuthoritativeSnapshotAndOldSaveCannotRebaseReopenedDocument()
    {
        var buffer = new VirtualDocumentBuffer("a");
        var session = new DocumentSession();
        session.Reset(buffer.Text);
        buffer.Apply(buffer.Epoch, 2, [new(1, 0, "\n")]);
        var snapshot = buffer.VerifySnapshot(buffer.Epoch, 2, "a\n", false);
        var operation = session.TryBeginSave()!;
        buffer.Apply(buffer.Epoch, 3, [new(2, 0, "later")]);
        Assert.True(session.TryCommitSave(operation, snapshot.Text, default));
        session.EndSave(operation);
        Assert.False(session.MatchesBaseline(buffer.Text));
        Assert.False(session.CanClose(buffer.Text));
        var old = session.TryBeginSave()!;
        session.Reset("reopened");
        Assert.False(session.TryCommitSave(old, snapshot.Text, default));
        Assert.Equal("reopened", session.BaselineText);
    }
}
