using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Module.Archive.Tests;

public class ArchiveBatchPresentationTests
{
    private static BackgroundJobSnapshot Snapshot(Guid id, BackgroundJobState state, double progress = 0)
        => new(id, BackgroundJobKind.ExtractArchive, "Extract", "source.zip", "output",
            state, progress, state == BackgroundJobState.Failed ? "Failed safely" : null, null, default,
            state is BackgroundJobState.Succeeded or BackgroundJobState.Failed or BackgroundJobState.Canceled ? DateTimeOffset.UtcNow : null);

    [Theory]
    [InlineData(BackgroundJobState.Running, false, true, "Preparing")]
    [InlineData(BackgroundJobState.WaitingForPassword, false, true, "Jobs")]
    [InlineData(BackgroundJobState.Canceling, false, false, "Canceling")]
    [InlineData(BackgroundJobState.Succeeded, true, false, "Completed")]
    [InlineData(BackgroundJobState.Failed, false, false, "Failed")]
    [InlineData(BackgroundJobState.Canceled, false, false, "Canceled")]
    public void ResultActionsFollowRealJobState(BackgroundJobState state, bool open, bool cancel, string status)
    {
        var id = Guid.NewGuid();
        var model = new ArchiveBatchPresentation([new(id, Task.FromResult(Snapshot(id, state)))], ["source.zip"]);
        model.Update([Snapshot(id, state)]);
        var item = Assert.Single(model.Items);
        Assert.Equal(open, item.CanOpen);
        Assert.Equal(cancel, item.CanCancel);
        Assert.Contains(status, item.Status);
        Assert.Equal(state == BackgroundJobState.Running, item.IsIndeterminate);
    }

    [Fact]
    public async Task SlowWorkShowsProgressThenTerminalResultAndNotifiesExistingRow()
    {
        var service = new BackgroundJobService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = service.Start(new(BackgroundJobKind.ExtractArchive, "Extract", "source.zip", "output"), async context =>
        {
            context.Progress.Report(0.25);
            entered.SetResult();
            await release.Task;
        });
        var model = new ArchiveBatchPresentation([handle], ["source.zip"]);
        var row = model.Items[0];
        var changed = 0;
        row.PropertyChanged += (_, _) => changed++;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.Update(service.GetSnapshots());
            Assert.Equal(25, row.Progress);
            Assert.False(row.IsIndeterminate);
            Assert.False(row.CanOpen);
            Assert.True(row.CanCancel);
            release.SetResult();
            var completed = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            model.Update([completed]);
            model.Update([Snapshot(handle.Id, BackgroundJobState.Running, 0.1)]);
            Assert.Same(row, model.Items[0]);
            Assert.Equal(100, row.Progress);
            Assert.True(row.CanOpen);
            Assert.Equal("Completed", row.Status);
            Assert.Equal(2, changed);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task FastCompletionBeforeFirstRenderSurvivesHistoryEvictionAndKeepsSelectionOrder()
    {
        var service = new BackgroundJobService(historyLimit: 1);
        var inputs = Enumerable.Range(0, 100).Select(index =>
            (new BackgroundJobRequest(BackgroundJobKind.ExtractArchive, "Extract", $"{index}.zip", $"output/{index}"),
                (Func<BackgroundJobContext, Task>)(_ => Task.CompletedTask))).ToArray();
        var handles = service.StartMany(inputs);
        var completed = await Task.WhenAll(handles.Select(handle => handle.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(service.GetSnapshots());
        var model = new ArchiveBatchPresentation(handles, Enumerable.Range(0, 100).Select(index => $"{index}.zip").ToArray());
        model.Update(service.GetSnapshots());
        model.Update(completed.Reverse());
        Assert.Equal(100, model.Items.Count);
        Assert.All(model.Items, item => Assert.True(item.CanOpen));
        Assert.Equal(Enumerable.Range(0, 100).Select(index => $"{index}.zip"), model.Items.Select(item => item.Name));
        Assert.Contains("100/100 finished", model.Summary);
    }

    [Fact]
    public async Task CanceledViewObservationCanResumeAfterCompletionWasDismissedFromHistory()
    {
        var service = new BackgroundJobService();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = service.Start(new(BackgroundJobKind.ExtractArchive, "Extract", "source.zip", "output"),
            _ => release.Task);
        var model = new ArchiveBatchPresentation([handle], ["source.zip"]);
        model.Update(service.GetSnapshots());
        using var detached = new CancellationTokenSource();
        var oldObservation = model.WaitForCompletionAsync(handle.Id, detached.Token);
        detached.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldObservation);
        release.SetResult();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.Dismiss(handle.Id));
        Assert.Empty(service.GetSnapshots());
        using var reattached = new CancellationTokenSource();
        model.Update([await model.WaitForCompletionAsync(handle.Id, reattached.Token)]);
        Assert.Equal("Completed", model.Items[0].Status);
        Assert.True(model.Items[0].CanOpen);
        Assert.Equal("output", model.Items[0].ResultPath);
    }

    [Fact]
    public void UnrelatedJobsAndMissingSuccessfulResultCannotEnableOpen()
    {
        var id = Guid.NewGuid();
        var result = Snapshot(id, BackgroundJobState.Succeeded) with { ResultPath = null };
        var model = new ArchiveBatchPresentation([new(id, Task.FromResult(result))], ["source.zip"]);
        model.Update([Snapshot(Guid.NewGuid(), BackgroundJobState.Succeeded)]);
        Assert.Null(model.Items[0].Snapshot);
        model.Update([result]);
        Assert.False(model.Items[0].CanOpen);
    }
}
