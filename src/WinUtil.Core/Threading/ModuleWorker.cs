using System.Collections.Concurrent;

namespace WinUtil.Core.Threading;

/// <summary>
/// 모듈 전용 직렬 워커(A42): 이름 있는 전용 스레드 1개 + FIFO 큐.
/// 산발적인 임시 Task.Run을 대체한다 — 요청(Run)/취소(CancellationToken)/진행률(IProgress)/
/// 완료(Task)를 한 계약으로 통일하고, 같은 워커에 넣은 작업은 겹치지 않음(직렬·순서 보장)을 보장한다.
/// UI 스레드에서 await하면 완료 시 UI 스레드로 복귀하므로 뷰는 결과만 디스패치 받는다.
///
/// 수명: 뷰(또는 모듈)당 1개를 만들고 내려갈 때 Dispose. Dispose는 큐만 닫고 Join하지 않는다 —
/// 실행 중·대기 중 작업은 워커 스레드가 마저 처리한 뒤 스스로 종료한다(느린 I/O가 UI 해제를
/// 막으면 안 된다). 스레드는 IsBackground라 프로세스 종료도 막지 않는다.
/// </summary>
public sealed class ModuleWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    /// <param name="name">스레드 이름(디버거·덤프에서 식별용). 예: "ZP archive worker".</param>
    /// <param name="priority">사용자가 결과를 기다리는 작업은 Normal, 배경성 작업은 BelowNormal.</param>
    public ModuleWorker(string name, ThreadPriority priority = ThreadPriority.Normal)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name, Priority = priority };
        _thread.Start();
    }

    /// <summary>
    /// 작업을 큐에 넣고 완료 Task를 돌려준다. 차례가 오기 전에 취소되면 실행 없이 취소로 완료.
    /// 작업 안에서 던진 OperationCanceledException은 취소로, 그 외 예외는 Task 실패로 옮긴다.
    /// </summary>
    public Task<T> Run<T>(Func<WorkContext, T> work, CancellationToken cancellation = default,
        IProgress<double>? progress = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = TryAdd(() =>
        {
            if (cancellation.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellation);
                return;
            }
            try
            {
                tcs.TrySetResult(work(new WorkContext(cancellation, progress)));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellation.IsCancellationRequested ? cancellation : default);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!accepted) tcs.TrySetCanceled();
        return tcs.Task;
    }

    /// <summary>결과 없는 작업용 Run.</summary>
    public Task Run(Action<WorkContext> work, CancellationToken cancellation = default,
        IProgress<double>? progress = null)
        => Run<object?>(ctx => { work(ctx); return null; }, cancellation, progress);

    /// <summary>
    /// 완료를 기다리지 않는 뒷정리성 작업(fire-and-forget). 예외는 삼킨다 —
    /// libvlc 해제처럼 "실패해도 그만"인 정리 전용. 큐가 열려 있으면 Run과 같은 큐라
    /// 순서가 보장되고, Dispose로 닫힌 뒤라면 스레드풀로 폴백해 실행 자체는 보장한다
    /// (네이티브 해제가 조용히 버려져 누수되면 안 된다).
    /// </summary>
    public void Post(Action work)
    {
        void Guarded()
        {
            try { work(); }
            catch { /* 뒷정리 실패는 무시 */ }
        }

        if (!TryAdd(Guarded))
            ThreadPool.UnsafeQueueUserWorkItem(_ => Guarded(), null);
    }

    private bool TryAdd(Action item)
    {
        try
        {
            _queue.Add(item);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Dispose로 큐가 닫힌 뒤 — 조용히 거절(호출자에겐 취소된 Task).
            // CompleteAdding 후 Add는 IOE, Dispose 후는 ODE인데 ODE가 IOE의 파생이라
            // 이 catch 하나로 둘 다 잡힌다(따로 잡으면 CS0160).
            return false;
        }
    }

    private void Loop()
    {
        // Run은 예외를 Task로 옮기고 Post는 자체 차단하므로 여기로 새는 예외는 없다.
        foreach (var item in _queue.GetConsumingEnumerable())
            item();
        _queue.Dispose();
    }

    /// <summary>큐를 닫는다. 남은 작업은 워커가 마저 실행한 뒤 스레드가 끝난다(Join 안 함).</summary>
    public void Dispose()
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // 중복 Dispose 허용
        }
    }
}
