using KOTU.Core.Diagnostics;
using KOTU.Core.Integration;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class ContentTransitionJournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kotu-transition-tests", Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_folder, "transitions.log");

    [Fact]
    public void FlushesStagesAndOmitsUnknownModuleAndExceptionMessage()
    {
        using var journal = new ContentTransitionJournal(LogPath);
        journal.Record(1, 4, "audio", ContentTransitionStage.DetachBegin);
        journal.Record(1, 4, @"C:\private\secret.zip", ContentTransitionStage.Failed,
            new InvalidOperationException("private document text"));
        if (DistributionPolicy.IsStandalone)
        {
            Assert.False(Directory.Exists(_folder));
            return;
        }
        // 파일을 아직 닫지 않아도 종료 직전 단계가 읽혀야 한다.
        var lines = ReadOpenLog();
        Assert.Equal(2, lines.Length);
        Assert.Contains("view=1 generation=4 module=audio stage=DetachBegin", lines[0]);
        Assert.Contains("module=other stage=Failed error=InvalidOperationException hr=0x80131509", lines[1]);
        Assert.DoesNotContain("private", string.Join("\n", lines));
        Assert.DoesNotContain("secret", string.Join("\n", lines));
    }

    [Fact]
    public void ConcurrentWindowsKeepCompleteIdentifiableLines()
    {
        using var journal = new ContentTransitionJournal(LogPath);
        Parallel.For(1, 101, view => journal.Record(view, 1, "archive", ContentTransitionStage.OpenFailed));
        if (DistributionPolicy.IsStandalone)
        {
            Assert.False(Directory.Exists(_folder));
            return;
        }
        var lines = ReadOpenLog();
        Assert.Equal(100, lines.Length);
        for (var view = 1; view <= 100; view++)
            Assert.Single(lines, line => line.Contains($"view={view} generation=1 module=archive stage=OpenFailed"));
    }

    [Fact]
    public void RotationRetainsRecentStagesWithinTwoBoundedFiles()
    {
        using var journal = new ContentTransitionJournal(LogPath);
        for (var generation = 1; generation <= 1500; generation++)
            journal.Record(1, generation, "image", ContentTransitionStage.Completed);
        if (DistributionPolicy.IsStandalone)
        {
            Assert.False(Directory.Exists(_folder));
            return;
        }
        Assert.InRange(new FileInfo(LogPath).Length, 1, ContentTransitionJournal.MaxBytes);
        Assert.InRange(new FileInfo(LogPath + ".1").Length, 1, ContentTransitionJournal.MaxBytes);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
        Assert.Contains("generation=1500", ReadOpenLog().Last());
    }

    [Fact]
    public void DisabledOrClosedJournalNeverCreatesFiles()
    {
        using var disabled = new ContentTransitionJournal(LogPath, enabled: false);
        disabled.Record(1, 1, "audio", ContentTransitionStage.Begin);
        var closed = new ContentTransitionJournal(LogPath);
        closed.Dispose();
        closed.Record(1, 1, "audio", ContentTransitionStage.Begin);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void UnwritableDestinationDoesNotThrowOrRetryAfterFailure()
    {
        Directory.CreateDirectory(_folder);
        // 파일 대신 디렉터리인 목적지로 쓰기 실패를 결정적으로 만든다.
        Directory.CreateDirectory(LogPath);
        using var journal = new ContentTransitionJournal(LogPath);
        journal.Record(1, 1, "audio", ContentTransitionStage.Begin);
        Directory.Delete(LogPath);
        journal.Record(1, 2, "archive", ContentTransitionStage.Begin);
        Assert.False(File.Exists(LogPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string[] ReadOpenLog()
    {
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
