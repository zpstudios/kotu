namespace KOTU.Core.Threading;

/// <summary>작업의 소유권을 완료와 대기 포기 중 한 곳에만 넘기는 시한 대기.</summary>
public static class BoundedOperation
{
    public static async Task<T> WaitAsync<T>(Task<T> operation, TimeSpan timeout,
        CancellationToken cancellation = default, Action? cancel = null, Action<T>? discard = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (operation.IsCompleted) return await operation.ConfigureAwait(false);

        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Register(() => stop.TrySetResult(true));
        using var timer = new Timer(_ => stop.TrySetResult(false), null, timeout, Timeout.InfiniteTimeSpan);
        var winner = await Task.WhenAny(operation, stop.Task).ConfigureAwait(false);
        if (ReferenceEquals(winner, operation)) return await operation.ConfigureAwait(false);

        // 취소 콜백 자체가 막혀도 호출자의 대기 종료를 가로막지 않는다.
        if (cancel is not null) _ = Task.Run(() => { try { cancel(); } catch { } });
        _ = Task.Run(() => ObserveDiscardedAsync(operation, discard));
        if (await stop.Task.ConfigureAwait(false)) throw new OperationCanceledException(cancellation);
        throw new TimeoutException("The operation did not complete within the allowed time.");
    }

    private static async Task ObserveDiscardedAsync<T>(Task<T> operation, Action<T>? discard)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            discard?.Invoke(result);
        }
        catch
        {
            // 포기한 작업의 실패도 관측한다. 정리 실패는 이미 정해진 결과를 바꾸지 않는다.
        }
    }
}
