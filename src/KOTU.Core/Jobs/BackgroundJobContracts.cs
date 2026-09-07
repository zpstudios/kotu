namespace KOTU.Core.Jobs;

public enum BackgroundJobState { Running, WaitingForPassword, Canceling, Succeeded, Failed, Canceled }
public enum BackgroundJobKind { ExtractArchive, CreateArchive, OpenArchiveEntry }

/// <summary>입력 경로와 결과 위치만 공개한다. 암호나 실행 델리게이트는 이 기록에 넣지 않는다.</summary>
public sealed record BackgroundJobRequest(BackgroundJobKind Kind, string Title, string? SourcePath, string? ResultPath);

public sealed record BackgroundJobSnapshot(Guid Id, BackgroundJobKind Kind, string Title,
    string? SourcePath, string? ResultPath, BackgroundJobState State, double Progress,
    string? Error, Guid? PasswordRequestId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public bool IsActive => State is BackgroundJobState.Running or BackgroundJobState.WaitingForPassword or BackgroundJobState.Canceling;
}

public sealed record BackgroundJobHandle(Guid Id, Task<BackgroundJobSnapshot> Completion);

/// <summary>워커가 사용하는 실행 맥락. UI를 보관하지 않으며 암호는 현재 시도에만 전달한다.</summary>
public sealed class BackgroundJobContext
{
    private readonly Func<Task<string>> _requestPassword;
    public CancellationToken Cancellation { get; }
    public IProgress<double> Progress { get; }

    internal BackgroundJobContext(CancellationToken cancellation, IProgress<double> progress, Func<Task<string>> requestPassword)
    {
        Cancellation = cancellation;
        Progress = progress;
        _requestPassword = requestPassword;
    }

    public Task<string> RequestPasswordAsync() => _requestPassword();
}

