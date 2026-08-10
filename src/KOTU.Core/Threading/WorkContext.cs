namespace KOTU.Core.Threading;

/// <summary>
/// 워커에서 실행 중인 작업에 전달되는 실행 맥락(A42) — 취소와 진행률 보고를 한 형태로 통일한다.
/// 백엔드 코드는 이 맥락만 알면 되고, UI 마샬링은 호출자가 넘긴 IProgress 구현이 책임진다
/// (UI 스레드에서 만든 <see cref="Progress{T}"/>는 SynchronizationContext로 자동 마샬링).
/// </summary>
public sealed class WorkContext
{
    private static readonly IProgress<double> NullProgress = new NoopProgress();

    internal WorkContext(CancellationToken cancellation, IProgress<double>? progress)
    {
        Cancellation = cancellation;
        Progress = progress ?? NullProgress;
    }

    /// <summary>취소 요청 토큰. 장기 루프는 주기적으로 확인해야 한다.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>진행률 보고 대상(0..1). 호출자가 안 넘겼으면 no-op 싱크.</summary>
    public IProgress<double> Progress { get; }

    /// <summary>취소됐으면 OperationCanceledException을 던진다.</summary>
    public void ThrowIfCancelled() => Cancellation.ThrowIfCancellationRequested();

    private sealed class NoopProgress : IProgress<double>
    {
        public void Report(double value) { }
    }
}
