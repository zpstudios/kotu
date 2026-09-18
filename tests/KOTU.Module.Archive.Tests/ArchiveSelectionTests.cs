using System.Collections.Concurrent;
using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Module.Archive.Tests;

public class ArchiveSelectionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("single.txt")]
    [InlineData("folder")]
    public void ExplicitFolderNeverUsesTheSingleRootShortcut(string root)
    {
        var path = Path.Combine(Path.GetTempPath(), "name.zip");
        var auto = ExtractHerePlanner.Plan(path, [root], _ => false);
        var explicitPlan = ExtractHerePlanner.Plan(path, [root], _ => false, forceFolder: true);
        Assert.Equal(Path.GetDirectoryName(path), auto.TargetDirectory);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(path)!, "name"), explicitPlan.TargetDirectory);
        Assert.Equal(explicitPlan.TargetDirectory, explicitPlan.ResultPath);
    }

    [Fact]
    public async Task AllJobsExistWhileFirstListIsBlockedAndFailureDoesNotDropOthers()
    {
        var service = new BackgroundJobService();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extracted = new ConcurrentBag<string>();
        var backend = new Backend
        {
            Read = (path, _) =>
            {
                if (path.EndsWith("bad.zip", StringComparison.Ordinal)) throw new IOException();
                entered.TrySetResult(); release.Wait(); return [];
            },
            Unpack = (path, _, _) => extracted.Add(path)
        };
        var coordinator = new ArchiveJobCoordinator(service, backend);
        string[] paths = ["good.zip", "bad.zip", "another.zip"];
        var handles = coordinator.StartExtractMany(paths, true);
        paths[0] = "mutated.zip";
        try
        {
            await entered.Task.WaitAsync(Limit);
            Assert.Equal(3, service.GetSnapshots().Count);
            Assert.True(service.HasActiveJobs);
            release.Set();
            var results = await Task.WhenAll(handles.Select(handle => handle.Completion)).WaitAsync(Limit);
            Assert.Equal(2, results.Count(result => result.State == BackgroundJobState.Succeeded));
            Assert.Single(results, result => result.State == BackgroundJobState.Failed);
            Assert.Contains("good.zip", extracted);
            Assert.Contains("another.zip", extracted);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ConcurrentAutomaticResultsReserveDistinctPathsAcrossCoordinators()
    {
        var service = new BackgroundJobService();
        var parent = Path.Combine(Path.GetTempPath(), "kotu-reserve-" + Guid.NewGuid());
        using var release = new ManualResetEventSlim();
        var targets = new ConcurrentBag<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend
        {
            Read = (_, _) => [new("shared.txt", false, 1, default)],
            Unpack = (_, target, _) =>
            {
                targets.Add(target);
                if (targets.Count == 2) entered.TrySetResult();
                release.Wait();
            }
        };
        var first = new ArchiveJobCoordinator(service, backend).StartExtractMany([Path.Combine(parent, "one.zip")], false);
        var second = new ArchiveJobCoordinator(service, backend).StartExtractMany([Path.Combine(parent, "two.zip")], false);
        try
        {
            await entered.Task.WaitAsync(Limit);
            var snapshots = service.GetSnapshots();
            Assert.Equal(2, snapshots.Select(job => job.ResultPath).Distinct().Count());
            Assert.Contains(Path.Combine(parent, "shared.txt"), snapshots.Select(job => job.ResultPath));
            Assert.Contains(snapshots, job => job.ResultPath == Path.Combine(parent, "one") || job.ResultPath == Path.Combine(parent, "two"));
            Assert.Equal(2, targets.Distinct().Count());
        }
        finally { release.Set(); await Task.WhenAll(first.Concat(second).Select(handle => handle.Completion)).WaitAsync(Limit); }
    }

    [Fact]
    public async Task OpenArchivesPasswordIsReusedForPlanning()
    {
        var service = new BackgroundJobService();
        string? received = null;
        var backend = new Backend
        {
            Read = (_, password) =>
            {
                received = password;
                if (password != "known secret") throw new ArchivePasswordException();
                return [];
            }
        };
        var result = await new ArchiveJobCoordinator(service, backend)
            .StartExtractMany(["already-open.7z"], true, "known secret")[0].Completion.WaitAsync(Limit);
        Assert.Equal(BackgroundJobState.Succeeded, result.State);
        Assert.Equal("known secret", received);
        Assert.DoesNotContain("known secret", result.ToString());
    }

    [Fact]
    public async Task CancelingOnePasswordWaitDoesNotCancelOtherArchives()
    {
        var service = new BackgroundJobService();
        var waiting = new TaskCompletionSource<BackgroundJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            var snapshot = service.GetSnapshots().FirstOrDefault(job => job.State == BackgroundJobState.WaitingForPassword);
            if (snapshot is not null) waiting.TrySetResult(snapshot);
        };
        var backend = new Backend { Read = (path, _) => path == "locked.zip" ? throw new ArchivePasswordException() : [] };
        var handles = new ArchiveJobCoordinator(service, backend).StartExtractMany(["locked.zip", "open.zip"], true);
        var challenge = await waiting.Task.WaitAsync(Limit);
        Assert.True(service.Cancel(challenge.Id));
        Assert.False(service.ProvidePassword(challenge.Id, challenge.PasswordRequestId!.Value, "obsolete"));
        var results = await Task.WhenAll(handles.Select(handle => handle.Completion)).WaitAsync(Limit);
        Assert.Equal(BackgroundJobState.Canceled, results[0].State);
        Assert.Equal(BackgroundJobState.Succeeded, results[1].State);
    }

    private sealed class Backend : IArchiveBackend
    {
        public Func<string, string?, IReadOnlyList<ArchiveEntry>> Read = (_, _) => [];
        public Action<string, string, CancellationToken> Unpack = (_, _, _) => { };
        public IReadOnlyList<ArchiveEntry> List(string archivePath, string? password = null) => Read(archivePath, password);
        public void Extract(string archivePath, string targetDirectory, IReadOnlyCollection<string>? entryPaths = null,
            string? password = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
            => Unpack(archivePath, targetDirectory, cancellationToken);
        public void CreateZip(IReadOnlyList<string> sourcePaths, string archivePath, string? password = null,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Create7z(IReadOnlyList<string> sourcePaths, string archivePath, string? password = null,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
