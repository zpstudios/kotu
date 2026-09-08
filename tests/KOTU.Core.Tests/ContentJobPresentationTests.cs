using KOTU.Core.Contracts;
using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Core.Tests;

public class ContentJobPresentationTests
{
    private sealed class Owner(Guid? id) : IBackgroundJobOwner { public Guid? ActiveJobId => id; }
    private static BackgroundJobSnapshot Job(Guid id, BackgroundJobState state, double progress = 0.45)
        => new(id, BackgroundJobKind.ExtractArchive, "Extract", "a.zip", "a", state, progress,
            null, null, DateTimeOffset.UtcNow, null);

    [Theory]
    [InlineData(BackgroundJobState.Running)]
    [InlineData(BackgroundJobState.WaitingForPassword)]
    [InlineData(BackgroundJobState.Canceling)]
    public void Only_current_contents_active_job_preserves_the_window(BackgroundJobState state)
    {
        var own = Job(Guid.NewGuid(), state);
        var other = Job(Guid.NewGuid(), BackgroundJobState.Running);
        Assert.Same(own, ContentJobPresentation.FindActive(new Owner(own.Id), [other, own]));
        Assert.Null(ContentJobPresentation.FindActive(new Owner(own.Id), [other]));
        Assert.Null(ContentJobPresentation.FindActive(new object(), [own, other]));
    }

    [Theory]
    [InlineData(BackgroundJobState.Succeeded)]
    [InlineData(BackgroundJobState.Failed)]
    [InlineData(BackgroundJobState.Canceled)]
    public void Terminal_jobs_neither_redirect_nor_leave_progress_in_title(BackgroundJobState state)
    {
        var job = Job(Guid.NewGuid(), state);
        Assert.Null(ContentJobPresentation.FindActive(new Owner(job.Id), [job]));
        Assert.Empty(ContentJobPresentation.TitleStatus(job));
    }

    [Fact]
    public void Title_has_ten_segments_and_distinct_waiting_state()
    {
        var job = Job(Guid.NewGuid(), BackgroundJobState.WaitingForPassword);
        Assert.Equal("[■■■■□□□□□□ 45% · Password needed]", ContentJobPresentation.TitleStatus(job));
        Assert.Equal("[■■■■□□□□□□ 45% · Canceling]",
            ContentJobPresentation.TitleStatus(job with { State = BackgroundJobState.Canceling }));
    }

    [Theory]
    [InlineData(double.NaN, "[□□□□□□□□□□ 0%]")]
    [InlineData(-1, "[□□□□□□□□□□ 0%]")]
    [InlineData(2, "[■■■■■■■■■■ 100%]")]
    public void Title_clamps_invalid_progress(double progress, string expected)
        => Assert.Equal(expected, ContentJobPresentation.TitleStatus(Job(Guid.NewGuid(), BackgroundJobState.Running, progress)));
}
