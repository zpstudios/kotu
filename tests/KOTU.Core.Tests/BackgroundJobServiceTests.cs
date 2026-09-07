using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Core.Tests;

public class BackgroundJobServiceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static BackgroundJobRequest Request(string title = "Extract") =>
        new(BackgroundJobKind.ExtractArchive, title, "source.zip", "result");

    private static async Task<BackgroundJobSnapshot> WaitForAsync(BackgroundJobService jobs,
        Guid id, Func<BackgroundJobSnapshot, bool> condition)
    {
        var ready = Signal<BackgroundJobSnapshot>();
        void Check()
        {
            var snapshot = jobs.GetSnapshots().FirstOrDefault(job => job.Id == id);
            if (snapshot is not null && condition(snapshot)) ready.TrySetResult(snapshot);
        }
        jobs.Changed += Check;
        try
        {
            Check();
            return await ready.Task.WaitAsync(Limit);
        }
        finally { jobs.Changed -= Check; }
    }

    [Fact]
    public async Task ProgressIsMonotonicFiniteAndIgnoredAfterCompletion()
    {
        var jobs = new BackgroundJobService();
        var ready = Signal<BackgroundJobContext>();
        var finish = Signal<bool>();
        var handle = jobs.Start(Request(), async context =>
        {
            context.Progress.Report(.6);
            context.Progress.Report(.2);
            context.Progress.Report(double.NaN);
            context.Progress.Report(double.PositiveInfinity);
            ready.SetResult(context);
            await finish.Task;
        });
        try
        {
            var context = await ready.Task.WaitAsync(Limit);
            Assert.Equal(.6, Assert.Single(jobs.GetSnapshots()).Progress);
            finish.SetResult(true);
            var result = await handle.Completion.WaitAsync(Limit);
            Assert.Equal(BackgroundJobState.Succeeded, result.State);
            Assert.Equal(1, result.Progress);
            context.Progress.Report(.9);
            Assert.Equal(result, Assert.Single(jobs.GetSnapshots()));
        }
        finally { finish.TrySetResult(true); }
    }

    [Fact]
    public async Task CancelWaitsForCurrentWorkAndDoesNotTurnLateSuccessIntoSuccess()
    {
        var jobs = new BackgroundJobService();
        var entered = Signal<BackgroundJobContext>();
        var finishFile = Signal<bool>();
        var handle = jobs.Start(Request(), async context =>
        {
            entered.SetResult(context);
            await finishFile.Task;
        });
        try
        {
            var context = await entered.Task.WaitAsync(Limit);
            Assert.True(jobs.Cancel(handle.Id));
            Assert.True(context.Cancellation.IsCancellationRequested);
            Assert.Equal(BackgroundJobState.Canceling, Assert.Single(jobs.GetSnapshots()).State);
            Assert.True(jobs.HasActiveJobs);
            Assert.Null(jobs.TryAcquireIdleLease());
            Assert.False(handle.Completion.IsCompleted);
            Assert.False(jobs.Cancel(handle.Id));
            finishFile.SetResult(true);
            Assert.Equal(BackgroundJobState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
            Assert.False(jobs.HasActiveJobs);
            Assert.False(jobs.Cancel(handle.Id));
        }
        finally { finishFile.TrySetResult(true); }
    }

    [Fact]
    public async Task BlockingCancelCallbackCannotBlockCallerOrRaceSourceDisposal()
    {
        var jobs = new BackgroundJobService();
        var entered = Signal<bool>();
        var callbackEntered = Signal<bool>();
        var callbackFinished = Signal<bool>();
        var finishWork = Signal<bool>();
        using var releaseCallback = new ManualResetEventSlim();
        var handle = jobs.Start(Request(), async context =>
        {
            context.Cancellation.Register(() =>
            {
                callbackEntered.SetResult(true);
                releaseCallback.Wait();
                callbackFinished.SetResult(true);
            });
            entered.SetResult(true);
            await finishWork.Task;
        });
        try
        {
            await entered.Task.WaitAsync(Limit);
            Assert.True(jobs.Cancel(handle.Id));
            await callbackEntered.Task.WaitAsync(Limit);
            finishWork.SetResult(true);
            Assert.Equal(BackgroundJobState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
            Assert.False(callbackFinished.Task.IsCompleted);
        }
        finally
        {
            finishWork.TrySetResult(true);
            releaseCallback.Set();
            await callbackFinished.Task.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task PasswordChallengesRejectStaleAndDuplicateSubmissions()
    {
        var jobs = new BackgroundJobService();
        var received = new List<string>();
        var handle = jobs.Start(Request(), async context =>
        {
            received.Add(await context.RequestPasswordAsync());
            received.Add(await context.RequestPasswordAsync());
        });
        var first = await WaitForAsync(jobs, handle.Id, job => job.State == BackgroundJobState.WaitingForPassword);
        Assert.NotNull(first.PasswordRequestId);
        Assert.True(jobs.HasActiveJobs);
        Assert.True(jobs.ProvidePassword(handle.Id, first.PasswordRequestId.Value, "first secret"));
        Assert.False(jobs.ProvidePassword(handle.Id, first.PasswordRequestId.Value, "duplicate"));
        var second = await WaitForAsync(jobs, handle.Id, job =>
            job.State == BackgroundJobState.WaitingForPassword && job.PasswordRequestId != first.PasswordRequestId);
        Assert.False(jobs.ProvidePassword(handle.Id, first.PasswordRequestId.Value, "stale window"));
        Assert.True(jobs.ProvidePassword(handle.Id, second.PasswordRequestId!.Value, "second secret"));
        var result = await handle.Completion.WaitAsync(Limit);
        Assert.Equal(["first secret", "second secret"], received);
        Assert.Equal(BackgroundJobState.Succeeded, result.State);
        Assert.Null(result.PasswordRequestId);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(jobs.GetSnapshots()));
    }

    [Fact]
    public async Task CancelWaitingPasswordCompletesWithoutInput()
    {
        var jobs = new BackgroundJobService();
        var handle = jobs.Start(Request(), async context => { await context.RequestPasswordAsync(); });
        var waiting = await WaitForAsync(jobs, handle.Id, job => job.State == BackgroundJobState.WaitingForPassword);
        Assert.True(jobs.Cancel(handle.Id));
        Assert.False(jobs.ProvidePassword(handle.Id, waiting.PasswordRequestId!.Value, "late secret"));
        Assert.Equal(BackgroundJobState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
        Assert.False(jobs.HasActiveJobs);
    }

    [Fact]
    public async Task PasswordSubmissionAndCancelRaceStillFinalizeOnce()
    {
        var jobs = new BackgroundJobService();
        var finish = Signal<bool>();
        var handle = jobs.Start(Request(), async context =>
        {
            await context.RequestPasswordAsync();
            await finish.Task;
        });
        var waiting = await WaitForAsync(jobs, handle.Id, job => job.State == BackgroundJobState.WaitingForPassword);
        await Task.WhenAll(
            Task.Run(() => jobs.ProvidePassword(handle.Id, waiting.PasswordRequestId!.Value, "secret")),
            Task.Run(() => jobs.Cancel(handle.Id)));
        finish.SetResult(true);
        var result = await handle.Completion.WaitAsync(Limit);
        Assert.Equal(BackgroundJobState.Canceled, result.State);
        Assert.Single(jobs.GetSnapshots());
        Assert.Null(result.PasswordRequestId);
    }

    [Fact]
    public async Task FailedJobDoesNotPublishExceptionSecretsAndSubscriberFailureIsIsolated()
    {
        var jobs = new BackgroundJobService();
        var notifications = 0;
        jobs.Changed += () => throw new InvalidOperationException("view closed");
        jobs.Changed += () => Interlocked.Increment(ref notifications);
        var handle = jobs.Start(Request(), _ => throw new IOException("password=private123"));
        var result = await handle.Completion.WaitAsync(Limit);
        Assert.Equal(BackgroundJobState.Failed, result.State);
        Assert.DoesNotContain("private123", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.True(notifications > 0);
        Assert.True(jobs.Dismiss(handle.Id));
        Assert.Empty(jobs.GetSnapshots());
    }

    [Fact]
    public async Task HistoryCapNeverEvictsActiveJobs()
    {
        var jobs = new BackgroundJobService(historyLimit: 2);
        var finish = Signal<bool>();
        var active = jobs.Start(Request("active"), _ => finish.Task);
        try
        {
            for (var i = 0; i < 4; i++)
                await jobs.Start(Request("done " + i), _ => Task.CompletedTask).Completion.WaitAsync(Limit);
            var snapshots = jobs.GetSnapshots();
            Assert.Equal(3, snapshots.Count);
            Assert.Contains(snapshots, job => job.Id == active.Id && job.IsActive);
            Assert.False(jobs.Dismiss(active.Id));
            finish.SetResult(true);
            await active.Completion.WaitAsync(Limit);
            Assert.Equal(2, jobs.GetSnapshots().Count);
        }
        finally { finish.TrySetResult(true); }
    }

    [Fact]
    public async Task IdleLeaseAtomicallyBlocksNewStartsAndOldDisposalCannotReleaseNewLease()
    {
        var jobs = new BackgroundJobService();
        var first = jobs.TryAcquireIdleLease();
        Assert.NotNull(first);
        Assert.Null(jobs.TryAcquireIdleLease());
        Assert.Throws<InvalidOperationException>(() => jobs.Start(Request(), _ => Task.CompletedTask));
        first.Dispose();
        using var second = jobs.TryAcquireIdleLease();
        Assert.NotNull(second);
        first.Dispose();
        Assert.Throws<InvalidOperationException>(() => jobs.Start(Request(), _ => Task.CompletedTask));
        second.Dispose();
        Assert.Equal(BackgroundJobState.Succeeded,
            (await jobs.Start(Request(), _ => Task.CompletedTask).Completion.WaitAsync(Limit)).State);
    }

    [Fact]
    public async Task StartNotificationCanCancelBeforeWorkIsScheduled()
    {
        var jobs = new BackgroundJobService();
        var executed = false;
        jobs.Changed += () =>
        {
            foreach (var job in jobs.GetSnapshots().Where(job => job.State == BackgroundJobState.Running))
                jobs.Cancel(job.Id);
        };
        var handle = jobs.Start(Request(), _ => { executed = true; return Task.CompletedTask; });
        Assert.Equal(BackgroundJobState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
        Assert.False(executed);
    }
}

