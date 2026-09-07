using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Module.Archive.Tests;

public class ArchiveJobCoordinatorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<BackgroundJobSnapshot> WaitingAsync(BackgroundJobService jobs, Guid id, Guid? previous = null)
    {
        var ready = Signal<BackgroundJobSnapshot>();
        void Check()
        {
            var snapshot = jobs.GetSnapshots().FirstOrDefault(job => job.Id == id);
            if (snapshot?.State == BackgroundJobState.WaitingForPassword && snapshot.PasswordRequestId != previous)
                ready.TrySetResult(snapshot);
        }
        jobs.Changed += Check;
        try { Check(); return await ready.Task.WaitAsync(Limit); }
        finally { jobs.Changed -= Check; }
    }

    [Fact]
    public async Task ExtractionCopiesSelectedEntriesAndContinuesAfterObserverLeaves()
    {
        var jobs = new BackgroundJobService();
        var backend = new FakeBackend();
        var coordinator = new ArchiveJobCoordinator(jobs, backend);
        var entered = Signal<bool>();
        using var release = new ManualResetEventSlim();
        string[]? extracted = null;
        backend.ExtractAction = (_, _, entries, _, progress, _) =>
        {
            entered.SetResult(true);
            release.Wait();
            extracted = entries!.ToArray();
            progress!.Report(1);
        };
        string[] selected = ["original/file.txt"];
        using var observation = new CancellationTokenSource();
        var handle = coordinator.StartExtract("original.zip", "target", selected);
        try
        {
            await entered.Task.WaitAsync(Limit);
            selected[0] = "replacement/file.txt";
            var waitingView = handle.Completion.WaitAsync(observation.Token);
            observation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingView);
            Assert.True(jobs.HasActiveJobs);
            Assert.False(handle.Completion.IsCompleted);
            release.Set();
            var result = await handle.Completion.WaitAsync(Limit);
            Assert.Equal(BackgroundJobState.Succeeded, result.State);
            Assert.NotNull(extracted);
            Assert.Equal(["original/file.txt"], extracted);
            Assert.Equal("original.zip", result.SourcePath);
            Assert.Equal("target", result.ResultPath);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task PasswordRetriesUseGlobalChallengesWithoutViewCallbacks()
    {
        var jobs = new BackgroundJobService();
        var backend = new FakeBackend();
        var coordinator = new ArchiveJobCoordinator(jobs, backend);
        var passwords = new List<string?>();
        backend.ExtractAction = (_, _, _, password, _, _) =>
        {
            passwords.Add(password);
            if (password != "correct secret") throw new ArchivePasswordException();
        };
        var handle = coordinator.StartExtract("locked.7z", "target", password: "wrong initial");
        var first = await WaitingAsync(jobs, handle.Id);
        Assert.True(jobs.ProvidePassword(handle.Id, first.PasswordRequestId!.Value, "wrong retry"));
        var second = await WaitingAsync(jobs, handle.Id, first.PasswordRequestId);
        Assert.False(jobs.ProvidePassword(handle.Id, first.PasswordRequestId!.Value, "stale secret"));
        Assert.True(jobs.ProvidePassword(handle.Id, second.PasswordRequestId!.Value, "correct secret"));
        var result = await handle.Completion.WaitAsync(Limit);
        Assert.Equal(BackgroundJobState.Succeeded, result.State);
        Assert.Equal(["wrong initial", "wrong retry", "correct secret"], passwords);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(jobs.GetSnapshots()));
    }

    [Fact]
    public async Task CancelAtPasswordPromptNeverStartsAnotherNativeAttempt()
    {
        var jobs = new BackgroundJobService();
        var backend = new FakeBackend();
        var attempts = 0;
        backend.ExtractAction = (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new ArchivePasswordException();
        };
        var handle = new ArchiveJobCoordinator(jobs, backend).StartExtract("locked.zip", "target");
        var waiting = await WaitingAsync(jobs, handle.Id);
        Assert.True(jobs.Cancel(handle.Id));
        Assert.False(jobs.ProvidePassword(handle.Id, waiting.PasswordRequestId!.Value, "late"));
        Assert.Equal(BackgroundJobState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancelAllowsCurrentFileToFinishAndKeepsPartialOutputs()
    {
        var jobs = new BackgroundJobService();
        var backend = new FakeBackend();
        var entered = Signal<bool>();
        using var release = new ManualResetEventSlim();
        var output = new List<string>();
        backend.ExtractAction = (_, _, _, _, _, token) =>
        {
            entered.SetResult(true);
            release.Wait();
            output.Add("current file");
            if (!token.IsCancellationRequested) output.Add("next file");
        };
        var handle = new ArchiveJobCoordinator(jobs, backend).StartExtract("source.zip", "target");
        try
        {
            await entered.Task.WaitAsync(Limit);
            jobs.Cancel(handle.Id);
            Assert.False(handle.Completion.IsCompleted);
            release.Set();
            Assert.Equal(BackgroundJobState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
            Assert.Equal(["current file"], output);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationCopiesSourcesAndUsesWorkerForBothFormats(bool use7z)
    {
        var jobs = new BackgroundJobService();
        var backend = new FakeBackend();
        var coordinator = new ArchiveJobCoordinator(jobs, backend);
        var entered = Signal<bool>();
        using var release = new ManualResetEventSlim();
        var ownerThread = Environment.CurrentManagedThreadId;
        string[]? observed = null;
        var workerThread = ownerThread;
        var observedFormat = !use7z;
        backend.CreateAction = (sources, path, password, format, _, _) =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            entered.SetResult(true);
            release.Wait();
            observed = sources.ToArray();
            observedFormat = format;
            Assert.Equal("new.archive", path);
            Assert.Equal("creation secret", password);
        };
        string[] sources = ["first.txt", "second.txt"];
        var handle = coordinator.StartCreate(sources, "new.archive", use7z, "creation secret");
        try
        {
            await entered.Task.WaitAsync(Limit);
            sources[0] = "changed.txt";
            release.Set();
            var result = await handle.Completion.WaitAsync(Limit);
            Assert.Equal(BackgroundJobState.Succeeded, result.State);
            Assert.NotNull(observed);
            Assert.Equal(["first.txt", "second.txt"], observed);
            Assert.NotEqual(ownerThread, workerThread);
            Assert.Equal(use7z, observedFormat);
            Assert.Equal(BackgroundJobKind.CreateArchive, result.Kind);
            Assert.DoesNotContain("creation secret", System.Text.Json.JsonSerializer.Serialize(result));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task TemporaryEntryResultRemainsAccessibleWithoutLaunchingAnything()
    {
        var jobs = new BackgroundJobService();
        var backend = new FakeBackend { ExtractAction = (_, _, _, _, _, _) => { } };
        var handle = new ArchiveJobCoordinator(jobs, backend).StartExtract("source.zip", "temporary",
            ["entry.txt"], resultPath: "temporary/entry.txt", openEntry: true);
        var result = await handle.Completion.WaitAsync(Limit);
        Assert.Equal(BackgroundJobKind.OpenArchiveEntry, result.Kind);
        Assert.Equal("temporary/entry.txt", result.ResultPath);
        Assert.Equal(BackgroundJobState.Succeeded, result.State);
    }

    [Fact]
    public void ClosingLeaseRejectsCoordinatorStartBeforeAnyNativeCall()
    {
        var jobs = new BackgroundJobService();
        var backend = new FakeBackend();
        using var lease = jobs.TryAcquireIdleLease();
        Assert.NotNull(lease);
        var coordinator = new ArchiveJobCoordinator(jobs, backend);
        Assert.Throws<InvalidOperationException>(() => coordinator.StartExtract("source.zip", "target"));
        Assert.Throws<InvalidOperationException>(() => coordinator.StartCreate(["source"], "new.zip", false));
        Assert.Empty(jobs.GetSnapshots());
    }

    private sealed class FakeBackend : IArchiveBackend
    {
        public Action<string, string, IReadOnlyCollection<string>?, string?, IProgress<double>?, CancellationToken>? ExtractAction;
        public Action<IReadOnlyList<string>, string, string?, bool, IProgress<double>?, CancellationToken>? CreateAction;
        public IReadOnlyList<ArchiveEntry> List(string archivePath, string? password = null) => [];
        public void Extract(string archivePath, string targetDirectory, IReadOnlyCollection<string>? entryPaths = null,
            string? password = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            (ExtractAction ?? throw new InvalidOperationException("Unexpected extraction"))(
                archivePath, targetDirectory, entryPaths, password, progress, cancellationToken);
        public void CreateZip(IReadOnlyList<string> sourcePaths, string archivePath, string? password = null,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            (CreateAction ?? throw new InvalidOperationException("Unexpected creation"))(
                sourcePaths, archivePath, password, false, progress, cancellationToken);
        public void Create7z(IReadOnlyList<string> sourcePaths, string archivePath, string? password = null,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            (CreateAction ?? throw new InvalidOperationException("Unexpected creation"))(
                sourcePaths, archivePath, password, true, progress, cancellationToken);
    }
}
