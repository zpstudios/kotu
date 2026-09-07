namespace KOTU.Core.Jobs;

/// <summary>
/// 앱 수명의 작업 목록. 잠금 밖에서 알리고 완료 이력만 제한한다.
/// 취소는 협조적이며 실행 중 파일이나 이미 생성한 결과를 강제로 제거하지 않는다.
/// </summary>
public sealed class BackgroundJobService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly int _historyLimit;
    private IdleLease? _idleLease;
    public event Action? Changed;

    public BackgroundJobService(int historyLimit = 50) => _historyLimit = Math.Max(1, historyLimit);

    public bool HasActiveJobs
    {
        get { lock (_gate) return _jobs.Values.Any(job => job.Snapshot.IsActive); }
    }

    public IReadOnlyList<BackgroundJobSnapshot> GetSnapshots()
    {
        lock (_gate) return _jobs.Values.Select(job => job.Snapshot).OrderByDescending(job => job.CreatedAt).ToArray();
    }

    public BackgroundJobHandle Start(BackgroundJobRequest request, Func<BackgroundJobContext, Task> execute)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execute);
        Job job;
        lock (_gate)
        {
            if (_idleLease is not null) throw new InvalidOperationException("The app is closing or restarting. New jobs cannot be started.");
            job = new Job(request);
            _jobs.Add(job.Snapshot.Id, job);
        }
        NotifyChanged();
        // 서비스가 보관하는 실행 델리게이트는 불변 작업 입력만 캡처해야 한다.
        _ = Task.Run(() => ExecuteAsync(job, execute));
        return new BackgroundJobHandle(job.Snapshot.Id, job.Completion.Task);
    }

    /// <summary>유휴 상태를 원자적으로 확보한다. UI 대기 중 잠금 없이 새 작업 시작만 막는다.</summary>
    public IDisposable? TryAcquireIdleLease()
    {
        lock (_gate)
        {
            if (_idleLease is not null || _jobs.Values.Any(job => job.Snapshot.IsActive)) return null;
            _idleLease = new IdleLease(this);
            return _idleLease;
        }
    }

    public bool Cancel(Guid id)
    {
        Job job;
        TaskCompletionSource<string>? password;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job!) || !job.Snapshot.IsActive ||
                job.Snapshot.State == BackgroundJobState.Canceling) return false;
            password = job.Password;
            job.Password = null;
            job.CancelInProgress = true;
            job.Snapshot = job.Snapshot with { State = BackgroundJobState.Canceling, PasswordRequestId = null };
        }
        _ = CancelAsync(job);
        password?.TrySetCanceled(job.Token);
        NotifyChanged();
        return true;
    }

    private async Task CancelAsync(Job job)
    {
        try
        {
            // .NET 8: 요청 표시는 즉시 세우고 등록 콜백은 호출한 UI 스레드 밖에서 실행한다.
            await job.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException) { }
        finally
        {
            bool dispose;
            lock (_gate)
            {
                job.CancelInProgress = false;
                dispose = !job.Snapshot.IsActive;
            }
            if (dispose) job.Cancellation.Dispose();
        }
    }

    public bool ProvidePassword(Guid id, Guid requestId, string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        TaskCompletionSource<string> waiting;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) ||
                job.Snapshot.State != BackgroundJobState.WaitingForPassword ||
                job.Snapshot.PasswordRequestId != requestId || job.Password is null) return false;
            waiting = job.Password;
            job.Password = null;
            job.Snapshot = job.Snapshot with { State = BackgroundJobState.Running, PasswordRequestId = null };
        }
        waiting.TrySetResult(password);
        NotifyChanged();
        return true;
    }

    public bool Dismiss(Guid id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Snapshot.IsActive) return false;
            _jobs.Remove(id);
        }
        NotifyChanged();
        return true;
    }

    private async Task ExecuteAsync(Job job, Func<BackgroundJobContext, Task> execute)
    {
        var state = BackgroundJobState.Succeeded;
        string? error = null;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            var context = new BackgroundJobContext(job.Token, new JobProgress(this, job),
                () => RequestPasswordAsync(job));
            await execute(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state = BackgroundJobState.Canceled;
        }
        catch
        {
            state = BackgroundJobState.Failed;
            // 백엔드 예외에 입력 암호가 포함돼도 공개 이력에 남기지 않는다.
            error = "The operation failed. The source or destination may be unavailable, damaged, or unsupported.";
        }

        BackgroundJobSnapshot completed;
        bool dispose;
        TaskCompletionSource<string>? password;
        lock (_gate)
        {
            // 취소를 먼저 수락했다면 현재 파일의 완료를 성공으로 덮어쓰지 않는다.
            if (job.Snapshot.State == BackgroundJobState.Canceling)
            {
                state = BackgroundJobState.Canceled;
                error = null;
            }
            password = job.Password;
            job.Password = null;
            completed = job.Snapshot with
            {
                State = state, Error = error, PasswordRequestId = null,
                Progress = state == BackgroundJobState.Succeeded ? 1 : job.Snapshot.Progress,
                CompletedAt = DateTimeOffset.UtcNow
            };
            job.Snapshot = completed;
            dispose = !job.CancelInProgress;
            foreach (var old in _jobs.Values.Where(value => !value.Snapshot.IsActive)
                         .OrderByDescending(value => value.Snapshot.CompletedAt).Skip(_historyLimit).ToArray())
                _jobs.Remove(old.Snapshot.Id);
        }
        password?.TrySetCanceled();
        if (dispose) job.Cancellation.Dispose();
        job.Completion.TrySetResult(completed);
        NotifyChanged();
    }

    private Task<string> RequestPasswordAsync(Job job)
    {
        TaskCompletionSource<string> waiting;
        lock (_gate)
        {
            if (!job.Snapshot.IsActive || job.Snapshot.State == BackgroundJobState.Canceling ||
                job.Token.IsCancellationRequested)
                return Task.FromCanceled<string>(new CancellationToken(true));
            if (job.Password is not null) return job.Password.Task;
            waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            job.Password = waiting;
            job.Snapshot = job.Snapshot with
            {
                State = BackgroundJobState.WaitingForPassword, PasswordRequestId = Guid.NewGuid()
            };
        }
        NotifyChanged();
        return waiting.Task;
    }

    private void Report(Job job, double value)
    {
        if (!double.IsFinite(value)) return;
        lock (_gate)
        {
            if (job.Snapshot.State is not (BackgroundJobState.Running or BackgroundJobState.Canceling)) return;
            var progress = Math.Clamp(value, job.Snapshot.Progress, 1);
            if (progress == job.Snapshot.Progress) return;
            job.Snapshot = job.Snapshot with { Progress = progress };
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { /* 닫힌 화면의 알림 실패가 실행 중 작업을 실패시키면 안 된다. */ }
        }
    }

    private sealed class Job
    {
        public BackgroundJobSnapshot Snapshot;
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token { get; }
        public TaskCompletionSource<BackgroundJobSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string>? Password;
        public bool CancelInProgress;

        public Job(BackgroundJobRequest request)
        {
            Token = Cancellation.Token;
            Snapshot = new(Guid.NewGuid(), request.Kind, request.Title, request.SourcePath,
                request.ResultPath, BackgroundJobState.Running, 0, null, null, DateTimeOffset.UtcNow, null);
        }
    }

    private sealed class JobProgress(BackgroundJobService service, Job job) : IProgress<double>
    {
        public void Report(double value) => service.Report(job, value);
    }

    private sealed class IdleLease(BackgroundJobService service) : IDisposable
    {
        public void Dispose()
        {
            lock (service._gate)
            {
                if (ReferenceEquals(service._idleLease, this)) service._idleLease = null;
            }
        }
    }
}
