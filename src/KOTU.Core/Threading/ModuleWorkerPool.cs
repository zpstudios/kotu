namespace KOTU.Core.Threading;

/// <summary>
/// ModuleWorker N개 고정 풀(A194): 항목별로 독립인 작업(썸네일·상세 조각 fetch 등)을
/// 상한 있는 병렬로 돌린다. Run은 라운드로빈으로 워커 하나에 배정한다.
/// <para>
/// 계약은 ModuleWorker를 준용한다 — 같은 워커에 배정된 작업끼리는 FIFO 직렬,
/// UI 스레드에서 await하면 완료 시 UI 스레드로 복귀, Dispose 뒤의 Run은 취소된 Task.
/// 단 <b>풀 안 배정 간(다른 워커에 배정된 작업 사이)의 실행·완료 순서는 보장하지 않는다</b> —
/// 순서 의존 작업(폴더 스캔처럼 결과가 상태의 원본이 되는 것)은 풀이 아니라
/// 단일 ModuleWorker에 넣어야 한다.
/// </para>
/// 수명: 뷰(또는 모듈)당 1개를 만들고 내려갈 때 Dispose — 모든 워커에 전파된다
/// (각 워커는 ModuleWorker 규칙대로 남은 큐를 마저 처리하고 스스로 종료한다).
/// </summary>
public sealed class ModuleWorkerPool : IDisposable
{
    private readonly ModuleWorker[] _workers;
    private int _next;

    /// <param name="namePrefix">워커 스레드 이름 접두사 — 뒤에 " 1"부터 " N"까지 붙는다.
    /// 예: "KOTU explorer fetch" → "KOTU explorer fetch 1..3".</param>
    /// <param name="count">고정 워커 수(= 최대 병렬 수). 1 이상.</param>
    /// <param name="priority">ModuleWorker와 같은 의미 — 사용자가 결과를 기다리면 Normal.</param>
    public ModuleWorkerPool(string namePrefix, int count, ThreadPriority priority = ThreadPriority.Normal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _workers = new ModuleWorker[count];
        for (var i = 0; i < count; i++)
            _workers[i] = new ModuleWorker($"{namePrefix} {i + 1}", priority);
    }

    /// <summary>작업을 라운드로빈으로 워커 하나에 배정한다. 반환 Task의 의미는 ModuleWorker.Run과 같다.</summary>
    public Task<T> Run<T>(Func<WorkContext, T> work, CancellationToken cancellation = default,
        IProgress<double>? progress = null)
        => NextWorker().Run(work, cancellation, progress);

    /// <summary>결과 없는 작업용 Run.</summary>
    public Task Run(Action<WorkContext> work, CancellationToken cancellation = default,
        IProgress<double>? progress = null)
        => NextWorker().Run(work, cancellation, progress);

    /// <summary>다음 배정 워커. UI 스레드 단독 호출이 전제지만 어긋나도 깨지지 않게 원자 증가로 돈다.</summary>
    private ModuleWorker NextWorker()
        => _workers[(uint)Interlocked.Increment(ref _next) % (uint)_workers.Length];

    /// <summary>모든 워커에 Dispose 전파. 닫힌 뒤의 Run은 취소된 Task를 돌려준다(ModuleWorker 계약).</summary>
    public void Dispose()
    {
        foreach (var worker in _workers) worker.Dispose();
    }
}
