using KOTU.Core.Contracts;

namespace KOTU.Core.Jobs;

/// <summary>현재 콘텐츠의 작업만 새 창 전환과 제목 진행 표시에 사용한다.</summary>
public static class ContentJobPresentation
{
    public static BackgroundJobSnapshot? FindActive(object? content, IReadOnlyList<BackgroundJobSnapshot> jobs)
        => content is IBackgroundJobOwner { ActiveJobId: { } id }
            ? jobs.FirstOrDefault(job => job.Id == id && job.IsActive)
            : null;

    public static string TitleStatus(BackgroundJobSnapshot? job)
    {
        if (job is not { IsActive: true }) return string.Empty;
        var progress = double.IsFinite(job.Progress) ? Math.Clamp(job.Progress, 0, 1) : 0;
        var percent = (int)(progress * 100);
        var filled = percent / 10;
        var bars = new string('■', filled) + new string('□', 10 - filled);
        var state = job.State switch
        {
            BackgroundJobState.WaitingForPassword => " · Password needed",
            BackgroundJobState.Canceling => " · Canceling",
            _ => string.Empty,
        };
        return $"[{bars} {percent}%{state}]";
    }
}
