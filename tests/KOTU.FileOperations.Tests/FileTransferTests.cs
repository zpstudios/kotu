using System.Diagnostics;
using Xunit;

namespace KOTU.FileOperations.Tests;

public sealed class FileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kotu-transfer-{Guid.NewGuid():N}");
    private readonly FileTransferService _service = new();
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    public FileTransferTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
    }

    private static Task<TransferDecision?> Decide(TransferChoice choice, bool all = false) =>
        Task.FromResult<TransferDecision?>(new(choice, all));

    private static Task<TransferDecision?> NoConflict(TransferConflict _) =>
        throw new InvalidOperationException("Unexpected conflict dialog");

    private static string Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileTransferPreservesBytesAndOnlyMoveRemovesSource(bool move)
    {
        var file = Write(Source, "sample.txt", "한글\r\nfile contents");
        var bytes = File.ReadAllBytes(file);
        var result = await _service.TransferAsync([file], Target, move, NoConflict);
        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Target, "sample.txt")));
        Assert.Equal(!move, File.Exists(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderTransferIncludesNestedHiddenFilesAndEmptyFolders(bool move)
    {
        var folder = Path.Combine(Source, "tree");
        var file = Write(Path.Combine(folder, "nested"), "hidden.txt", "hidden content");
        File.SetAttributes(file, FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var result = await _service.TransferAsync([folder], Target, move, NoConflict);
        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.Equal("hidden content", File.ReadAllText(Path.Combine(Target, "tree/nested/hidden.txt")));
        Assert.True(Directory.Exists(Path.Combine(Target, "tree/empty")));
        Assert.Equal(!move, Directory.Exists(folder));
    }

    [Fact]
    public async Task SameFolderMoveSkipsAndCopyCreatesNumberedSibling()
    {
        var file = Write(Source, "sample.txt", "original");
        var move = await _service.TransferAsync([file], Source, true, NoConflict);
        Assert.Equal(1, move.Skipped);
        Assert.True(move.SourcesRemain);
        var copy = await _service.TransferAsync([file], Source, false, NoConflict);
        Assert.Equal(1, copy.Done);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Source, "sample (2).txt")));
    }

    [Fact]
    public async Task FolderInsideItselfIsRejectedBeforeWriting()
    {
        Write(Source, "original.txt", "original");
        var result = await _service.TransferAsync([Source], Path.Combine(Source, "nested"), true, NoConflict);
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(Path.Combine(Source, "original.txt")));
        Assert.False(Directory.Exists(Path.Combine(Source, "nested")));
    }

    [Theory]
    [InlineData(TransferChoice.Replace, "new", false)]
    [InlineData(TransferChoice.Skip, "old", true)]
    [InlineData(TransferChoice.KeepBoth, "old", false)]
    public async Task FileConflictPoliciesPreserveTheirMoveContracts(TransferChoice choice, string expected, bool remains)
    {
        var file = Write(Source, "sample.txt", "new");
        Write(Target, "sample.txt", "old");
        var result = await _service.TransferAsync([file], Target, true, _ => Decide(choice));
        Assert.Equal(0, result.Failed);
        Assert.Equal(expected, File.ReadAllText(Path.Combine(Target, "sample.txt")));
        Assert.Equal(remains, File.Exists(file));
        Assert.Equal(remains, result.SourcesRemain);
        if (choice == TransferChoice.KeepBoth)
            Assert.Equal("new", File.ReadAllText(Path.Combine(Target, "sample (2).txt")));
    }

    [Fact]
    public async Task StickyChoiceIsScopedToOneOperation()
    {
        var files = new[] { Write(Source, "a.txt", "new-a"), Write(Source, "b.txt", "new-b") };
        Write(Target, "a.txt", "old-a");
        Write(Target, "b.txt", "old-b");
        var asks = 0;
        var first = await _service.TransferAsync(files, Target, false, _ => { asks++; return Decide(TransferChoice.Skip, true); });
        Assert.Equal(1, asks);
        Assert.Equal(2, first.Skipped);
        await _service.TransferAsync([files[0]], Target, false, _ => { asks++; return Decide(TransferChoice.Replace); });
        Assert.Equal(2, asks);
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(Target, "a.txt")));
    }

    [Fact]
    public async Task MergeSkipsKeepOriginalChildAndTargetOnlyChild()
    {
        var folder = Path.Combine(Source, "tree");
        var destination = Path.Combine(Target, "tree");
        Write(folder, "skip.txt", "source");
        Write(folder, "move.txt", "move");
        Write(destination, "skip.txt", "destination");
        Write(destination, "only-target.txt", "keep");
        var result = await _service.TransferAsync([folder], Target, true,
            c => Decide(c.IsFolder ? TransferChoice.Replace : TransferChoice.Skip));
        Assert.Equal(0, result.Failed);
        Assert.True(result.SourcesRemain);
        Assert.Equal("source", File.ReadAllText(Path.Combine(folder, "skip.txt")));
        Assert.Equal("destination", File.ReadAllText(Path.Combine(destination, "skip.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "only-target.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "move.txt")));
        Assert.Equal("move", File.ReadAllText(Path.Combine(destination, "move.txt")));
    }

    [Fact]
    public async Task CancelledConflictKeepsSourceAndStopsRemainingItems()
    {
        var first = Write(Source, "a.txt", "a");
        var second = Write(Source, "b.txt", "b");
        Write(Target, "a.txt", "old");
        var result = await _service.TransferAsync([first, second], Target, true,
            _ => Task.FromResult<TransferDecision?>(null));
        Assert.True(result.Cancelled);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(Target, "b.txt")));
    }

    [Fact]
    public async Task SourceEditedDuringConflictIsNeverDeletedOrPublished()
    {
        var file = Write(Source, "sample.txt", "old source");
        Write(Target, "sample.txt", "old destination");
        var result = await _service.TransferAsync([file], Target, true, _ =>
        {
            File.WriteAllText(file, "newly edited source with different size");
            return Decide(TransferChoice.Replace);
        });
        Assert.Equal(1, result.Failed);
        Assert.Equal("newly edited source with different size", File.ReadAllText(file));
        Assert.Equal("old destination", File.ReadAllText(Path.Combine(Target, "sample.txt")));
    }

    [Fact]
    public async Task SourceReplacedWithMatchingSizeAndTimestampStillFailsIdentityCheck()
    {
        var file = Write(Source, "sample.txt", "original");
        var timestamp = File.GetLastWriteTimeUtc(file);
        Write(Target, "sample.txt", "destination");
        var result = await _service.TransferAsync([file], Target, true, _ =>
        {
            File.Move(file, Path.Combine(Source, "old-file.txt"));
            File.WriteAllText(file, "replaced");
            File.SetLastWriteTimeUtc(file, timestamp);
            return Decide(TransferChoice.Replace);
        });
        Assert.Equal(1, result.Failed);
        Assert.Equal("replaced", File.ReadAllText(file));
        Assert.Equal("destination", File.ReadAllText(Path.Combine(Target, "sample.txt")));
    }

    [Fact]
    public async Task DestinationEditedDuringConflictIsNotOverwritten()
    {
        var file = Write(Source, "sample.txt", "source");
        var destination = Write(Target, "sample.txt", "old");
        var result = await _service.TransferAsync([file], Target, true, _ =>
        {
            File.WriteAllText(destination, "new destination from another operation");
            return Decide(TransferChoice.Replace);
        });
        Assert.Equal(1, result.Failed);
        Assert.Equal("source", File.ReadAllText(file));
        Assert.Equal("new destination from another operation", File.ReadAllText(destination));
    }

    [Fact]
    public async Task AddedChildDuringFolderMoveSurvivesCleanup()
    {
        var folder = Path.Combine(Source, "tree");
        Write(folder, "planned.txt", "planned");
        Directory.CreateDirectory(Path.Combine(Target, "tree"));
        var result = await _service.TransferAsync([folder], Target, true, _ =>
        {
            Write(folder, "new.txt", "arrived after planning");
            return Decide(TransferChoice.Replace);
        });
        Assert.True(result.SourcesRemain);
        Assert.Equal("arrived after planning", File.ReadAllText(Path.Combine(folder, "new.txt")));
        Assert.Equal("planned", File.ReadAllText(Path.Combine(Target, "tree/planned.txt")));
        Assert.False(File.Exists(Path.Combine(Target, "tree/new.txt")));
    }

    [Fact]
    public async Task LockedSourceFailsIndividuallyAndNextItemStillTransfers()
    {
        var locked = Write(Source, "locked.txt", "locked");
        var good = Write(Source, "good.txt", "good");
        using var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await _service.TransferAsync([locked, good], Target, true, NoConflict);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Done);
        Assert.True(File.Exists(locked));
        Assert.Equal("good", File.ReadAllText(Path.Combine(Target, "good.txt")));
    }

    [Fact]
    public async Task ReadOnlyDestinationFailurePreservesBothFiles()
    {
        var file = Write(Source, "sample.txt", "new");
        var destination = Write(Target, "sample.txt", "old");
        File.SetAttributes(destination, FileAttributes.ReadOnly);
        try
        {
            var result = await _service.TransferAsync([file], Target, true, _ => Decide(TransferChoice.Replace));
            Assert.Equal(1, result.Failed);
            Assert.Equal("new", File.ReadAllText(file));
            Assert.Equal("old", File.ReadAllText(destination));
            Assert.Single(Directory.GetFiles(Target));
        }
        finally { File.SetAttributes(destination, FileAttributes.Normal); }
    }

    [Fact]
    public async Task FileFolderMismatchDoesNotDestroyEitherSide()
    {
        var file = Write(Source, "same", "source");
        Directory.CreateDirectory(Path.Combine(Target, "same"));
        var result = await _service.TransferAsync([file], Target, true, _ => Decide(TransferChoice.Replace));
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(Path.Combine(Target, "same")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionSourceOrDestinationIsRejectedWithoutTouchingReferencedData(bool targetLink)
    {
        var real = Path.Combine(_root, "real");
        Write(real, "valuable.txt", "keep");
        var link = Path.Combine(_root, "junction");
        CreateJunction(link, real);
        try
        {
            var file = Write(Source, "new.txt", "new");
            var result = await _service.TransferAsync(targetLink ? [file] : [link], targetLink ? link : Target, true, NoConflict);
            Assert.Equal(1, result.Failed);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(real, "valuable.txt")));
            Assert.False(File.Exists(Path.Combine(real, "new.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task NestedJunctionIsRejectedBeforeAnyTreeCopy()
    {
        var folder = Path.Combine(Source, "tree");
        Write(folder, "normal.txt", "normal");
        var link = Path.Combine(folder, "loop");
        CreateJunction(link, folder);
        try
        {
            var result = await _service.TransferAsync([folder], Target, true, NoConflict);
            Assert.Equal(1, result.Failed);
            Assert.True(File.Exists(Path.Combine(folder, "normal.txt")));
            Assert.False(Directory.Exists(Path.Combine(Target, "tree")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task CancelDuringMergeRetainsRemainingSourceAndCompletedWork()
    {
        var folder = Path.Combine(Source, "tree");
        var destination = Path.Combine(Target, "tree");
        Write(folder, "a.txt", "new-a");
        Write(folder, "b.txt", "new-b");
        Write(destination, "a.txt", "old-a");
        Write(destination, "b.txt", "old-b");
        string? completed = null;
        var result = await _service.TransferAsync([folder], Target, true, c =>
        {
            if (c.IsFolder) return Decide(TransferChoice.Replace);
            if (completed is null) { completed = c.Name; return Decide(TransferChoice.Replace); }
            return Task.FromResult<TransferDecision?>(null);
        });
        Assert.True(result.Cancelled);
        Assert.True(result.SourcesRemain);
        Assert.NotNull(completed);
        Assert.False(File.Exists(Path.Combine(folder, completed)));
        Assert.Single(Directory.GetFiles(folder));
        Assert.Equal(2, Directory.GetFiles(destination).Length);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotCreateDestinationContent()
    {
        var file = Write(Source, "sample.txt", "source");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await _service.TransferAsync([file], Target, true, NoConflict, cancellationToken: cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(Target));
    }

    [Fact]
    public async Task SourceAncestorJunctionIsRejected()
    {
        var file = Write(Source, "sample.txt", "keep");
        var link = Path.Combine(_root, "alias");
        CreateJunction(link, Source);
        try
        {
            var result = await _service.TransferAsync([Path.Combine(link, "sample.txt")], Target, true, NoConflict);
            Assert.Equal(1, result.Failed);
            Assert.Equal("keep", File.ReadAllText(file));
            Assert.Empty(Directory.GetFiles(Target));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task DestinationJunctionIntroducedDuringConflictIsRejected()
    {
        var file = Write(Source, "sample.txt", "source");
        var destination = Write(Target, "sample.txt", "old");
        var outside = Path.Combine(_root, "outside");
        Write(outside, "valuable.txt", "keep");
        var result = await _service.TransferAsync([file], Target, true, _ =>
        {
            File.Delete(destination);
            CreateJunction(destination, outside);
            return Decide(TransferChoice.Replace);
        });
        try
        {
            Assert.Equal(1, result.Failed);
            Assert.Equal("source", File.ReadAllText(file));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "valuable.txt")));
        }
        finally { Directory.Delete(destination); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopiedMoveUsesVerifiedCleanupForFileAndFolder(bool folder)
    {
        var directory = folder ? Path.Combine(Source, "tree") : Source;
        var file = Write(directory, "sample.txt", "verified move");
        var path = folder ? directory : file;
        var result = await new FileTransferService(forceCopyForMoves: true)
            .TransferAsync([path], Target, true, NoConflict);
        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.False(result.SourcesRemain);
        Assert.False(File.Exists(file));
        Assert.Equal("verified move", File.ReadAllText(Path.Combine(Target, folder ? "tree/sample.txt" : "sample.txt")));
        if (folder) Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task MissingItemSkipsAndProgressKeepsTopLevelCounts()
    {
        var good = Write(Source, "good.txt", "good");
        var progress = new List<int>();
        var result = await _service.TransferAsync([Path.Combine(Source, "gone-parent/gone.txt"), good], Target, false,
            NoConflict, progress.Add);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Done);
        Assert.Equal(2, result.Total);
        Assert.Equal(new[] { 1, 2 }, progress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopiedFilePreservesNamedDataStreams(bool move)
    {
        var file = Write(Source, "streams.txt", "main stream");
        File.WriteAllText(file + ":metadata", "application metadata");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
        var result = await new FileTransferService(forceCopyForMoves: true)
            .TransferAsync([file], Target, move, NoConflict);
        Assert.True(result.Failed == 0, result.FirstError);
        Assert.Equal(1, result.Done);
        var destination = Path.Combine(Target, "streams.txt");
        Assert.Equal("main stream", File.ReadAllText(destination));
        Assert.Equal("application metadata", File.ReadAllText(destination + ":metadata"));
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3", File.ReadAllText(destination + ":Zone.Identifier"));
        Assert.Equal(!move, File.Exists(file));
    }

    [Fact]
    public async Task ReplaceMovePreservesNamedSourceStreams()
    {
        var file = Write(Source, "streams.txt", "new");
        File.WriteAllText(file + ":metadata", "important");
        var target = Write(Target, "streams.txt", "old");
        File.WriteAllText(target + ":target-only", "preserved target metadata");
        var result = await _service.TransferAsync([file], Target, true, _ => Decide(TransferChoice.Replace));
        Assert.True(result.Failed == 0, result.FirstError);
        Assert.False(File.Exists(file));
        Assert.Equal("important", File.ReadAllText(Path.Combine(Target, "streams.txt") + ":metadata"));
        Assert.Equal("preserved target metadata", File.ReadAllText(target + ":target-only"));
        Assert.Single(Directory.GetFiles(Target));
    }

    [Fact]
    public async Task DirectoryNamedStreamsAreNotSilentlyDeleted()
    {
        var folder = Path.Combine(Source, "tree");
        Write(folder, "normal.txt", "main");
        File.WriteAllText(folder + ":metadata", "folder metadata");
        var result = await new FileTransferService(forceCopyForMoves: true)
            .TransferAsync([folder], Target, true, NoConflict);
        Assert.Equal(1, result.Failed);
        Assert.Equal("folder metadata", File.ReadAllText(folder + ":metadata"));
        Assert.True(File.Exists(Path.Combine(folder, "normal.txt")));
        Assert.False(Directory.Exists(Path.Combine(Target, "tree")));
    }

    [Fact]
    public async Task AlternateSpellingOfSourceFolderCannotBypassSelfCopyGuard()
    {
        var folder = Path.Combine(Source, "tree");
        Write(folder, "keep.txt", "keep");
        var result = await _service.TransferAsync([folder], folder + ".", false, NoConflict);
        Assert.Equal(1, result.Failed);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(folder, "tree")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongPathsSupportNativeAndCopiedMoves(bool forceCopy)
    {
        var longSource = Path.Combine(Source, new string('a', 100), new string('b', 100), new string('c', 100));
        var longTarget = Path.Combine(Target, new string('x', 100), new string('y', 100), new string('z', 100));
        Directory.CreateDirectory(longTarget);
        var file = Write(longSource, "sample.txt", "long path");
        File.WriteAllText(file + ":metadata", "metadata");
        var result = await new FileTransferService(forceCopy).TransferAsync([file], longTarget, true, NoConflict);
        Assert.True(result.Failed == 0, result.FirstError);
        Assert.False(File.Exists(file));
        Assert.Equal("long path", File.ReadAllText(Path.Combine(longTarget, "sample.txt")));
        Assert.Equal("metadata", File.ReadAllText(Path.Combine(longTarget, "sample.txt") + ":metadata"));
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        // 테스트가 만든 고유 임시 폴더만 정리한다. 연결 폴더는 각 테스트가 먼저 비재귀 제거한다.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
